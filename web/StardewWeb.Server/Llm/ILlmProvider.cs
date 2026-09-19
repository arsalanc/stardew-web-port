using System.Text.Json.Nodes;

namespace StardewWeb.Server.Llm;

/// <summary>A model the chat panel can pick.</summary>
public record LlmModel(string Id, string Provider, bool Tools, bool Thinking, double SizeGb);

/// <summary>
/// A chat backend (Ollama now; Claude/Gemini later). The panel speaks one neutral format, modelled
/// on Ollama's chat API: messages { role: system|user|assistant|tool, content, tool_calls?, tool_name? }
/// and function-style tool schemas. Each provider converts to and from its own API.
/// </summary>
public interface ILlmProvider
{
	string Name { get; }

	Task<IReadOnlyList<LlmModel>> ListModelsAsync(CancellationToken ct);

	/// <summary>
	/// Streams the reply to <paramref name="output"/> as NDJSON lines in the neutral format:
	/// {"message":{"content":"…","thinking":"…","tool_calls":[{"function":{"name":"…","arguments":{…}}}]},"done":false}
	/// ending with a line whose "done" is true, or {"error":"…","done":true} on failure.
	/// </summary>
	Task StreamChatAsync(ChatRequest request, Stream output, CancellationToken ct);
}

/// <summary>A chat turn from the panel: the full history plus the tools the model may call.</summary>
public record ChatRequest(string Model, JsonArray Messages, JsonArray? Tools, bool? Think);
