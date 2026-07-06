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
        // Folders first so a note's FolderId always references a folder the
        // server already knows about.
        await PushFoldersAsync();
        await PushNotesAsync();
        await PullFoldersAsync();
        await PullNotesAsync();
    }

    private async Task PushFoldersAsync()
    {
        foreach (var folder in await _db.Folders.ToListAsync())
        {
            var response = await _http.PutAsJsonAsync($"/api/folders/{folder.Id}", folder);
            response.EnsureSuccessStatusCode();
        }
    }

    private async Task PushNotesAsync()
    {
        foreach (var note in await _db.Notes.ToListAsync())
        {
            var response = await _http.PutAsJsonAsync($"/api/notes/{note.Id}", note);
            response.EnsureSuccessStatusCode();
        }
    }

    private async Task PullFoldersAsync()
    {
        var remoteFolders = await _http.GetFromJsonAsync<List<Folder>>("/api/folders") ?? [];
        var localById = await _db.Folders.ToDictionaryAsync(f => f.Id);

        foreach (var remote in remoteFolders)
        {
            if (!localById.TryGetValue(remote.Id, out var local))
            {
                _db.Folders.Add(remote);
            }
            else if (remote.UpdatedAt > local.UpdatedAt)
            {
                local.Name = remote.Name;
                local.CreatedAt = remote.CreatedAt;
                local.UpdatedAt = remote.UpdatedAt;
                local.IsDeleted = remote.IsDeleted;
            }
        }

        await _db.SaveChangesAsync();
    }

    private async Task PullNotesAsync()
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
                local.FolderId = remote.FolderId;
                local.Tags = remote.Tags;
                local.Embedding = remote.Embedding;
                local.CreatedAt = remote.CreatedAt;
                local.UpdatedAt = remote.UpdatedAt;
                local.IsDeleted = remote.IsDeleted;
            }
        }

        await _db.SaveChangesAsync();
    }
}
