using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace NotesApp.Api.Services;

// Talks to a locally-running Ollama server (https://ollama.com). Ollama exposes a
// plain HTTP API, so there's no SDK to install â€” we just POST JSON to /api/generate.
public class OllamaAiService
{
    private readonly HttpClient _http;

    // The small, fast model we pulled with `ollama pull llama3.2`. Kept here so the
    // rest of the code doesn't hard-code the model name in multiple places.
    private const string Model = "llama3.2";

    // A dedicated embedding model (`ollama pull nomic-embed-text`). Embedding models
    // turn text into a vector of numbers so we can measure how similar two notes are.
    private const string EmbeddingModel = "nomic-embed-text";

    public OllamaAiService(HttpClient http)
    {
        _http = http;
    }

    // Shared low-level call: send a prompt, get back the model's completed text.
    private async Task<string> GenerateAsync(string prompt)
    {
        var request = new OllamaGenerateRequest(Model, prompt, Stream: false);

        var response = await _http.PostAsJsonAsync("/api/generate", request);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<OllamaGenerateResponse>();
        return result?.Response?.Trim() ?? string.Empty;
    }

    public Task<string> SummarizeAsync(string text) =>
        GenerateAsync(
            "Summarize the following note in 2-3 concise sentences. " +
            "Return only the summary, with no preamble.\n\n" + text);

    // Writes a brand-new note from a short instruction. We ask for a title on the
    // first line and the body after a blank line, then split the two apart.
    public async Task<DraftedNote> DraftNoteAsync(string prompt)
    {
        var raw = await GenerateAsync(
            "You are helping write a note. Based on the request below, write a note.\n" +
            "Put a short title on the FIRST line (no 'Title:' prefix, no markdown heading).\n" +
            "Then a blank line, then the note body in Markdown.\n\n" +
            "Request: " + prompt);

        var lines = raw.Split('\n');
        var title = lines.Length > 0 ? lines[0].Trim().TrimStart('#', ' ') : "Untitled";
        var body = lines.Length > 1 ? string.Join('\n', lines[1..]).Trim() : string.Empty;

        // If the model didn't leave a blank line, fall back to using it all as body.
        if (string.IsNullOrWhiteSpace(body))
        {
            body = raw;
        }

        return new DraftedNote(title, body);
    }

    // Transforms existing note text per an instruction (e.g. "make this more
    // concise", "fix grammar", "convert into a Markdown table").
    public Task<string> RewriteAsync(string text, string instruction) =>
        GenerateAsync(
            $"Rewrite the note below according to this instruction: {instruction}\n" +
            "Return only the rewritten note, with no preamble or explanation.\n\n" + text);

    // Turns text into an embedding vector via the embedding model's /api/embeddings.
    public async Task<float[]> EmbedAsync(string text)
    {
        var request = new OllamaEmbeddingRequest(EmbeddingModel, text);

        var response = await _http.PostAsJsonAsync("/api/embeddings", request);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<OllamaEmbeddingResponse>();
        return result?.Embedding ?? [];
    }
}

// Result of drafting a new note.
public record DraftedNote(string Title, string Body);

// Shapes of Ollama's /api/generate request and response. The JsonPropertyName
// attributes map our C# PascalCase names onto the lowercase keys Ollama expects.
public record OllamaGenerateRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("prompt")] string Prompt,
    [property: JsonPropertyName("stream")] bool Stream);

public record OllamaGenerateResponse(
    [property: JsonPropertyName("response")] string? Response);

public record OllamaEmbeddingRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("prompt")] string Prompt);

public record OllamaEmbeddingResponse(
    [property: JsonPropertyName("embedding")] float[]? Embedding);
