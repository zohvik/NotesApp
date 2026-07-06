using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using NotesApp.Client.Data;
using NotesApp.Core.Models;

namespace NotesApp.Client.Sync;

public class SyncService
{
    private readonly NotesDbContext _db;
    private readonly HttpClient _http;

    public SyncService(NotesDbContext db, HttpClient http)
    {
        _db = db;
        _http = http;
    }

    public async Task SyncAsync()
    {
        await PushAsync();
        await PullAsync();
    }

    private async Task PushAsync()
    {
        var localNotes = await _db.Notes.ToListAsync();

        foreach (var note in localNotes)
        {
            var response = await _http.PutAsJsonAsync($"/api/notes/{note.Id}", note);
            response.EnsureSuccessStatusCode();
        }
    }

    private async Task PullAsync()
    {
        var remoteNotes = await _http.GetFromJsonAsync<List<Note>>("/api/notes") ?? [];
        var localNotesById = await _db.Notes.ToDictionaryAsync(n => n.Id);

        foreach (var remote in remoteNotes)
        {
            if (!localNotesById.TryGetValue(remote.Id, out var local))
            {
                _db.Notes.Add(remote);
            }
            else if (remote.UpdatedAt > local.UpdatedAt)
            {
                local.Title = remote.Title;
                local.Body = remote.Body;
                local.CreatedAt = remote.CreatedAt;
                local.UpdatedAt = remote.UpdatedAt;
                local.IsDeleted = remote.IsDeleted;
            }
        }

        await _db.SaveChangesAsync();
    }
}
