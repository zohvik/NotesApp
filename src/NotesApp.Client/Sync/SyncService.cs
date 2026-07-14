using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using NotesApp.Client.Data;
using NotesApp.Core.Models;

namespace NotesApp.Client.Sync;

public class SyncService
{
    // Sync runs in the background, potentially at the same time as the UI is
    // reading/writing notes. EF Core contexts are NOT safe for concurrent use,
    // so instead of sharing the app-wide context, sync creates its own
    // short-lived context from this factory for each run.
    private readonly IDbContextFactory<NotesDbContext> _dbFactory;
    private readonly HttpClient _http;

    public SyncService(IDbContextFactory<NotesDbContext> dbFactory, HttpClient http)
    {
        _dbFactory = dbFactory;
        _http = http;
    }

    public async Task SyncAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        // Folders first so a note's FolderId always references a folder the
        // server already knows about.
        await PushFoldersAsync(db);
        await PushNotesAsync(db);
        await PullFoldersAsync(db);
        await PullNotesAsync(db);
    }

    private async Task PushFoldersAsync(NotesDbContext db)
    {
        foreach (var folder in await db.Folders.ToListAsync())
        {
            var response = await _http.PutAsJsonAsync($"/api/folders/{folder.Id}", folder);
            response.EnsureSuccessStatusCode();
        }
    }

    private async Task PushNotesAsync(NotesDbContext db)
    {
        foreach (var note in await db.Notes.ToListAsync())
        {
            var response = await _http.PutAsJsonAsync($"/api/notes/{note.Id}", note);
            response.EnsureSuccessStatusCode();
        }
    }

    private async Task PullFoldersAsync(NotesDbContext db)
    {
        var remoteFolders = await _http.GetFromJsonAsync<List<Folder>>("/api/folders") ?? [];
        var localById = await db.Folders.ToDictionaryAsync(f => f.Id);

        foreach (var remote in remoteFolders)
        {
            if (!localById.TryGetValue(remote.Id, out var local))
            {
                db.Folders.Add(remote);
            }
            else if (remote.UpdatedAt > local.UpdatedAt)
            {
                local.Name = remote.Name;
                local.CreatedAt = remote.CreatedAt;
                local.UpdatedAt = remote.UpdatedAt;
                local.IsDeleted = remote.IsDeleted;
            }
        }

        await db.SaveChangesAsync();
    }

    private async Task PullNotesAsync(NotesDbContext db)
    {
        var remoteNotes = await _http.GetFromJsonAsync<List<Note>>("/api/notes") ?? [];
        var localNotesById = await db.Notes.ToDictionaryAsync(n => n.Id);

        foreach (var remote in remoteNotes)
        {
            if (!localNotesById.TryGetValue(remote.Id, out var local))
            {
                db.Notes.Add(remote);
            }
            else if (remote.UpdatedAt > local.UpdatedAt)
            {
                local.Title = remote.Title;
                local.Body = remote.Body;
                local.FolderId = remote.FolderId;
                local.Tags = remote.Tags;
                local.CreatedAt = remote.CreatedAt;
                local.UpdatedAt = remote.UpdatedAt;
                local.IsDeleted = remote.IsDeleted;
            }
        }

        await db.SaveChangesAsync();
    }
}
