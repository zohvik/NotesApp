using System.Net.Http.Json;

namespace NotesApp.Client.Ai;

// Calls our own API's summarize endpoint. The client never talks to Ollama directly;
// it goes through NotesApp.Api, which owns the model connection (client -> API -> Ollama).
public class AiService
{
    private readonly HttpClient _http;

    public AiService(HttpClient http)
    {
        _http = http;
    }

    public async Task<string> SummarizeAsync(string text)
    {
        var response = await _http.PostAsJsonAsync("/api/ai/summarize", new { Text = text });
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<SummarizeResponse>();
        return result?.Summary ?? string.Empty;
    }

    // Asks the AI to write a new note (title + body) from a short instruction.
    public async Task<DraftResult> DraftAsync(string prompt)
    {
        var response = await _http.PostAsJsonAsync("/api/ai/draft", new { Prompt = prompt });
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<DraftResult>();
        return result ?? new DraftResult(string.Empty, string.Empty);
    }

    // Asks the AI to transform existing text per an instruction (edit / make table).
    public async Task<string> RewriteAsync(string text, string instruction)
    {
        var response = await _http.PostAsJsonAsync("/api/ai/rewrite",
            new { Text = text, Instruction = instruction });
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<RewriteResponse>();
        return result?.Result ?? string.Empty;
    }

    // Asks the API for notes semantically related to the given text.
    public async Task<List<RelatedNote>> GetRelatedAsync(Guid noteId, string text)
    {
        var response = await _http.PostAsJsonAsync("/api/ai/related",
            new { NoteId = noteId, Text = text });
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<List<RelatedNote>>() ?? [];
    }

    private record SummarizeResponse(string Summary);
    public record DraftResult(string Title, string Body);
    private record RewriteResponse(string Result);
}

// A note the AI found to be related, with a similarity score (1.0 = most similar).
// Top-level so XAML data templates can bind to it directly.
public record RelatedNote(Guid Id, string Title, double Score);
