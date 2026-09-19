using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using StardewWeb.Server.Llm;

var builder = WebApplication.CreateBuilder(args);

// In-game assistant backends. Local Ollama for now (--ollama <url>, --ollamaContext <tokens> to override).
builder.Services.AddSingleton<ILlmProvider>(_ => new OllamaProvider(
    new HttpClient
    {
        BaseAddress = new Uri(builder.Configuration["ollama"] ?? "http://localhost:11434/"),
        Timeout = Timeout.InfiniteTimeSpan,   // replies stream; cancellation comes from the browser
    },
    int.TryParse(builder.Configuration["ollamaContext"], out int ctx) ? ctx : 8192));

var app = builder.Build();

// Where the game's Content/ lives: --content <dir> overrides the path baked in at build time.
string contentDir = app.Configuration["content"]
    ?? typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .First(a => a.Key == "GameContentDir").Value!;
if (!Directory.Exists(contentDir))
{
    throw new DirectoryNotFoundException($"Game content folder not found: {contentDir}");
}
app.Logger.LogInformation("Serving game content from {ContentDir}", contentDir);

if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}

app.UseBlazorFrameworkFiles();

// The app's own files (page, scripts, styles): always revalidate, so an updated build or a patched
// script is picked up on a normal reload. Without this browsers may reuse a stale copy for hours.
// Game content and audio below keep normal caching; they're large and rarely change.
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache, must-revalidate",
});

// Game content: .xnb/.xwb/.tbin etc. aren't standard web types, so serve everything as binary.
var contentTypes = new FileExtensionContentTypeProvider();
contentTypes.Mappings[".json"] = "application/json";
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(contentDir),
    RequestPath = "/Content",
    ContentTypeProvider = contentTypes,
    ServeUnknownFileTypes = true,
    DefaultContentType = "application/octet-stream",
});

// Exported audio (tools/AudioExport): audio.json + one .wav per wave. Optional: without it the game is silent.
string audioDir = app.Configuration["audio"]
    ?? typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .First(a => a.Key == "GameAudioDir").Value!;
if (Directory.Exists(audioDir))
{
    app.Logger.LogInformation("Serving exported audio from {AudioDir}", audioDir);
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(audioDir),
        RequestPath = "/Audio",
        ContentTypeProvider = contentTypes,
    });
}
else
{
    app.Logger.LogWarning("No exported audio at {AudioDir}; run tools/AudioExport. The game will be silent.", audioDir);
}

// ---------- in-game assistant (wwwroot/js/chatPanel.js) ----------

app.MapGet("/api/llm/models", async (IEnumerable<ILlmProvider> providers, CancellationToken ct) =>
{
    var models = new List<LlmModel>();
    var errors = new List<string>();
    foreach (ILlmProvider provider in providers)
    {
        try { models.AddRange(await provider.ListModelsAsync(ct)); }
        catch (Exception ex) { errors.Add($"{provider.Name}: {ex.Message}"); }
    }
    return Results.Ok(new { models, errors });
});

app.MapPost("/api/llm/chat", async (HttpContext http, IEnumerable<ILlmProvider> providers) =>
{
    JsonObject? body = await http.Request.ReadFromJsonAsync<JsonObject>(http.RequestAborted);
    string providerName = (string?)body?["provider"] ?? "ollama";
    ILlmProvider? provider = providers.FirstOrDefault(p => p.Name == providerName);
    if (body?["model"] is null || body["messages"] is not JsonArray messages || provider is null)
    {
        return Results.BadRequest(new { error = "Expected { provider, model, messages[], tools?, think? } with a known provider." });
    }

    var request = new ChatRequest((string)body["model"]!, messages, body["tools"] as JsonArray, (bool?)body["think"]);
    http.Response.ContentType = "application/x-ndjson";
    try
    {
        await provider.StreamChatAsync(request, http.Response.Body, http.RequestAborted);
    }
    catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
    {
        // The player stopped the reply or closed the page.
    }
    catch (HttpRequestException ex)
    {
        await http.Response.WriteAsync(new JsonObject { ["error"] = $"Couldn't reach {provider.Name}: {ex.Message}", ["done"] = true }.ToJsonString() + "\n");
    }
    return Results.Empty;
});

app.MapFallbackToFile("index.html");
app.Run();
