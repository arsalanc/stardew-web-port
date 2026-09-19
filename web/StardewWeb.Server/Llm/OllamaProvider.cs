using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;

namespace StardewWeb.Server.Llm;

/// <summary>
/// Local models via Ollama (http://localhost:11434 by default). Ollama's chat API is the neutral
/// format, so replies stream through line by line.
/// </summary>
public sealed class OllamaProvider(HttpClient http, int contextTokens) : ILlmProvider
{
	public string Name => "ollama";

	public async Task<IReadOnlyList<LlmModel>> ListModelsAsync(CancellationToken ct)
	{
		JsonObject tags = await http.GetFromJsonAsync<JsonObject>("api/tags", ct) ?? new JsonObject();
		var models = new List<LlmModel>();
		foreach (JsonNode? m in tags["models"]?.AsArray() ?? new JsonArray())
		{
			string id = (string)m!["name"]!;
			// /api/show lists what the model can do ("tools", "thinking", ...).
			using var show = await http.PostAsJsonAsync("api/show", new { model = id }, ct);
			JsonArray caps = show.IsSuccessStatusCode
				? (await show.Content.ReadFromJsonAsync<JsonObject>(ct))?["capabilities"]?.AsArray() ?? new JsonArray()
				: new JsonArray();
			bool has(string c) => caps.Any(x => (string?)x == c);
			models.Add(new LlmModel(id, Name, has("tools"), has("thinking"), Math.Round((double)(m["size"] ?? 0) / (1 << 30), 1)));
		}
		return models;
	}

	public async Task StreamChatAsync(ChatRequest request, Stream output, CancellationToken ct)
	{
		var body = new JsonObject
		{
			["model"] = request.Model,
			["messages"] = request.Messages.DeepClone(),
			["stream"] = true,
			// Enough room for the tool schemas plus a few tool results, while keeping an 8B model on an 8 GB GPU.
			["options"] = new JsonObject { ["num_ctx"] = contextTokens },
		};
		if (request.Tools is { Count: > 0 })
		{
			body["tools"] = request.Tools.DeepClone();
		}
		// Only send "think" to models that support it (others reject the field).
		if (request.Think is bool think)
		{
			body["think"] = think;
		}

		using var message = new HttpRequestMessage(HttpMethod.Post, "api/chat")
		{
			Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
		};
		using HttpResponseMessage response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
		if (!response.IsSuccessStatusCode)
		{
			string detail = await response.Content.ReadAsStringAsync(ct);
			await WriteLineAsync(output, new JsonObject { ["error"] = $"Ollama returned {(int)response.StatusCode}: {detail}", ["done"] = true }.ToJsonString(), ct);
			return;
		}

		using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
		while (await reader.ReadLineAsync(ct) is string line)
		{
			if (line.Length > 0)
			{
				await WriteLineAsync(output, line, ct);
			}
		}
	}

	private static async Task WriteLineAsync(Stream output, string line, CancellationToken ct)
	{
		await output.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), ct);
		await output.FlushAsync(ct);
	}
}
