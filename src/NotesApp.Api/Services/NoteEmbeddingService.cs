using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NotesApp.Api.Data;
using NotesApp.Core.Models;

namespace NotesApp.Api.Services;

// Finds notes related to a piece of text using embeddings + cosine similarity.
// Each note's embedding is computed once and cached on the note (Note.Embedding),
// so repeat searches are cheap.
public class NoteEmbeddingService
{
    private readonly NotesDbContext _db;
    private readonly OllamaAiService _ai;

    public NoteEmbeddingService(NotesDbContext db, OllamaAiService ai)
    {
        _db = db;
        _ai = ai;
    }

    public async Task<List<RelatedNote>> FindRelatedAsync(Guid excludeNoteId, string queryText, int take = 5)
    {
        if (string.IsNullOrWhiteSpace(queryText))
        {
            return [];
        }

        var queryVector = await _ai.EmbedAsync(queryText);
        if (queryVector.Length == 0)
        {
            return [];
        }

        var candidates = await _db.Notes
            .Where(n => !n.IsDeleted && n.Id != excludeNoteId)
            .ToListAsync();

        var scored = new List<RelatedNote>();
        foreach (var note in candidates)
        {
            var vector = await EnsureEmbeddingAsync(note);
            if (vector.Length == 0)
            {
                continue;
            }

            var score = CosineSimilarity(queryVector, vector);
            scored.Add(new RelatedNote(note.Id, note.Title, score));
        }

        // 0.5 filters out notes that share almost nothing with the query. Above that,
        // return the closest matches first.
        return scored
            .Where(r => r.Score >= 0.5)
            .OrderByDescending(r => r.Score)
            .Take(take)
            .ToList();
    }

    // Returns a note's cached embedding, computing and saving it the first time.
    private async Task<float[]> EnsureEmbeddingAsync(Note note)
    {
        if (!string.IsNullOrEmpty(note.Embedding))
        {
            return JsonSerializer.Deserialize<float[]>(note.Embedding) ?? [];
        }

        var text = $"{note.Title}\n{note.Body}";
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var vector = await _ai.EmbedAsync(text);
        note.Embedding = JsonSerializer.Serialize(vector);
        await _db.SaveChangesAsync();
        return vector;
    }

    // Cosine similarity: 1.0 means identical direction, 0.0 means unrelated.
    private static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0)
        {
            return 0;
        }

        double dot = 0, magA = 0, magB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }

        if (magA == 0 || magB == 0)
        {
            return 0;
        }

        return dot / (Math.Sqrt(magA) * Math.Sqrt(magB));
    }
}

public record RelatedNote(Guid Id, string Title, double Score);
