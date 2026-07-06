using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using NotesApp.Client.Ai;
using NotesApp.Client.Data;
using NotesApp.Client.Sync;
using NotesApp.Core.Models;

namespace NotesApp.Client.ViewModels;

public partial class NotesViewModel : ObservableObject
{
    private readonly NotesDbContext _db;
    private readonly SyncService _syncService;
    private readonly AiService _aiService;

    // Sentinel folder pinned to the top of the sidebar. Selecting it shows every
    // note regardless of folder. Its Guid.Empty id is never written to the database.
    public static readonly Folder AllNotesFolder = new() { Id = Guid.Empty, Name = "All Notes" };

    public ObservableCollection<Folder> Folders { get; } = new();
    public ObservableCollection<Note> Notes { get; } = new();
    public ObservableCollection<RelatedNote> RelatedNotes { get; } = new();

    [ObservableProperty]
    private bool hasRelated;

    [ObservableProperty]
    private Folder? selectedFolder;

    [ObservableProperty]
    private Note? selectedNote;

    [ObservableProperty]
    private string editTitle = string.Empty;

    [ObservableProperty]
    private string editBody = string.Empty;

    [ObservableProperty]
    private string editTags = string.Empty;

    [ObservableProperty]
    private string newFolderName = string.Empty;

    [ObservableProperty]
    private string aiPrompt = string.Empty;

    [ObservableProperty]
    private string aiStatus = string.Empty;

    [ObservableProperty]
    private string syncStatus = "Not synced yet";

    // NotifyPropertyChangedFor regenerates a change notification for HasAiSummary
    // whenever AiSummary changes, so the summary panel shows/hides automatically.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAiSummary))]
    private string aiSummary = string.Empty;

    public bool HasAiSummary => !string.IsNullOrEmpty(AiSummary);

    public NotesViewModel(NotesDbContext db, SyncService syncService, AiService aiService)
    {
        _db = db;
        _syncService = syncService;
        _aiService = aiService;
    }

    partial void OnSelectedNoteChanged(Note? value)
    {
        EditTitle = value?.Title ?? string.Empty;
        EditBody = value?.Body ?? string.Empty;
        EditTags = value?.Tags ?? string.Empty;

        // Related results belong to the previously open note; clear them.
        RelatedNotes.Clear();
        HasRelated = false;
    }

    partial void OnSelectedFolderChanged(Folder? value)
    {
        // Switching folders re-filters the note list to that folder's notes.
        _ = LoadNotesAsync();
    }

    // ---- Loading -------------------------------------------------------------

    [RelayCommand]
    private async Task LoadDataAsync()
    {
        await _db.Database.EnsureCreatedAsync();
        await LoadFoldersAsync();
        await LoadNotesAsync();
    }

    private async Task LoadFoldersAsync()
    {
        var folders = (await _db.Folders
            .Where(f => !f.IsDeleted)
            .ToListAsync())
            .OrderBy(f => f.Name);

        Folders.Clear();
        Folders.Add(AllNotesFolder);
        foreach (var folder in folders)
        {
            Folders.Add(folder);
        }

        SelectedFolder ??= AllNotesFolder;
    }

    private async Task LoadNotesAsync()
    {
        var query = _db.Notes.Where(n => !n.IsDeleted);

        // Guid.Empty is the "All Notes" sentinel; any real id filters by folder.
        if (SelectedFolder is not null && SelectedFolder.Id != Guid.Empty)
        {
            var folderId = SelectedFolder.Id;
            query = query.Where(n => n.FolderId == folderId);
        }

        // SQLite can't ORDER BY DateTimeOffset, so sort in memory after fetching.
        var notes = (await query.ToListAsync()).OrderByDescending(n => n.UpdatedAt);

        Notes.Clear();
        foreach (var note in notes)
        {
            Notes.Add(note);
        }
    }

    // ---- Folder commands -----------------------------------------------------

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        if (string.IsNullOrWhiteSpace(NewFolderName))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var folder = new Folder
        {
            Name = NewFolderName.Trim(),
            CreatedAt = now,
            UpdatedAt = now
        };
        _db.Folders.Add(folder);
        await _db.SaveChangesAsync();

        Folders.Add(folder);
        NewFolderName = string.Empty;
        SelectedFolder = folder;
    }

    [RelayCommand]
    private async Task DeleteFolderAsync()
    {
        // Can't delete the "All Notes" sentinel.
        if (SelectedFolder is null || SelectedFolder.Id == Guid.Empty)
        {
            return;
        }

        var folderId = SelectedFolder.Id;
        var now = DateTimeOffset.UtcNow;

        // Unfile the folder's notes rather than deleting them, so no note is lost.
        var notesInFolder = await _db.Notes.Where(n => n.FolderId == folderId).ToListAsync();
        foreach (var note in notesInFolder)
        {
            note.FolderId = null;
            note.UpdatedAt = now;
        }

        SelectedFolder.IsDeleted = true;
        SelectedFolder.UpdatedAt = now;
        await _db.SaveChangesAsync();

        Folders.Remove(SelectedFolder);
        SelectedFolder = AllNotesFolder;
    }

    // ---- Note commands -------------------------------------------------------

    [RelayCommand]
    private void NewNote()
    {
        SelectedNote = null;
        EditTitle = string.Empty;
        EditBody = string.Empty;
        EditTags = string.Empty;
    }

    [RelayCommand]
    private async Task SaveNoteAsync()
    {
        if (string.IsNullOrWhiteSpace(EditTitle))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        Guid savedId;

        if (SelectedNote is null)
        {
            // New notes land in the currently selected folder (null when "All Notes").
            var folderId = (SelectedFolder is null || SelectedFolder.Id == Guid.Empty)
                ? (Guid?)null
                : SelectedFolder.Id;

            var note = new Note
            {
                Title = EditTitle,
                Body = EditBody,
                Tags = EditTags,
                FolderId = folderId,
                CreatedAt = now,
                UpdatedAt = now
            };
            _db.Notes.Add(note);
            await _db.SaveChangesAsync();
            savedId = note.Id;
        }
        else
        {
            SelectedNote.Title = EditTitle;
            SelectedNote.Body = EditBody;
            SelectedNote.Tags = EditTags;
            SelectedNote.UpdatedAt = now;
            await _db.SaveChangesAsync();
            savedId = SelectedNote.Id;
        }

        // Reload so the list reflects title changes (Note is a plain model and
        // doesn't raise change notifications on its own), then reselect the note.
        await LoadNotesAsync();
        SelectedNote = Notes.FirstOrDefault(n => n.Id == savedId);
    }

    [RelayCommand]
    private async Task DeleteNoteAsync()
    {
        if (SelectedNote is null)
        {
            return;
        }

        SelectedNote.IsDeleted = true;
        SelectedNote.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync();

        Notes.Remove(SelectedNote);
        NewNote();
    }

    // ---- Sync ----------------------------------------------------------------

    [RelayCommand]
    private async Task SyncAsync()
    {
        SyncStatus = "Syncing...";
        try
        {
            await _syncService.SyncAsync();
            await LoadFoldersAsync();
            await LoadNotesAsync();
            SyncStatus = $"Synced at {DateTimeOffset.Now:t}";
        }
        catch (HttpRequestException)
        {
            SyncStatus = "Sync failed - is the API reachable?";
        }
        catch (Exception ex)
        {
            // This command runs fire-and-forget from OnAppearing, so without this
            // catch-all an unexpected exception would vanish silently instead of
            // ever reaching the UI.
            SyncStatus = $"Sync failed - {ex.Message}";
        }
    }

    // ---- AI ------------------------------------------------------------------

    [RelayCommand]
    private async Task SummarizeAsync()
    {
        if (string.IsNullOrWhiteSpace(EditBody))
        {
            AiSummary = "Nothing to summarize yet.";
            return;
        }

        AiSummary = "Summarizing...";
        try
        {
            AiSummary = await _aiService.SummarizeAsync(EditBody);
        }
        catch (HttpRequestException)
        {
            AiSummary = "Summarize failed - is the API reachable?";
        }
        catch (TaskCanceledException)
        {
            // Thrown when the HttpClient timeout elapses before the model responds.
            AiSummary = "Summarize timed out - is Ollama running?";
        }
        catch (Exception ex)
        {
            AiSummary = $"Summarize failed - {ex.Message}";
        }
    }

    // Writes a brand-new note from the AiPrompt box into the editor (unsaved, so
    // the user can review and tweak before hitting Save).
    [RelayCommand]
    private async Task DraftAsync()
    {
        if (string.IsNullOrWhiteSpace(AiPrompt))
        {
            AiStatus = "Type what the AI should write.";
            return;
        }

        AiStatus = "Drafting...";
        try
        {
            var draft = await _aiService.DraftAsync(AiPrompt);
            NewNote();
            EditTitle = draft.Title;
            EditBody = draft.Body;
            AiStatus = "Draft ready - review and Save.";
        }
        catch (Exception ex)
        {
            AiStatus = DescribeAiError(ex);
        }
    }

    // Applies the AiPrompt as an editing instruction to the current note body.
    [RelayCommand]
    private async Task ApplyAiAsync()
    {
        if (string.IsNullOrWhiteSpace(EditBody))
        {
            AiStatus = "Nothing to edit yet.";
            return;
        }
        if (string.IsNullOrWhiteSpace(AiPrompt))
        {
            AiStatus = "Type an instruction (e.g. 'make more concise').";
            return;
        }

        AiStatus = "Editing...";
        try
        {
            EditBody = await _aiService.RewriteAsync(EditBody, AiPrompt);
            AiStatus = "Applied - review and Save.";
        }
        catch (Exception ex)
        {
            AiStatus = DescribeAiError(ex);
        }
    }

    // Converts the current note body into a Markdown table.
    [RelayCommand]
    private async Task MakeTableAsync()
    {
        if (string.IsNullOrWhiteSpace(EditBody))
        {
            AiStatus = "Nothing to tabulate yet.";
            return;
        }

        AiStatus = "Building table...";
        try
        {
            EditBody = await _aiService.RewriteAsync(
                EditBody,
                "Convert this into a clean Markdown table. Return only the table.");
            AiStatus = "Table ready - review and Save.";
        }
        catch (Exception ex)
        {
            AiStatus = DescribeAiError(ex);
        }
    }

    // Finds notes related to the current one (must be saved so it has an id).
    [RelayCommand]
    private async Task FindRelatedAsync()
    {
        if (SelectedNote is null)
        {
            AiStatus = "Open a saved note first, then find related notes.";
            return;
        }

        AiStatus = "Finding related notes...";
        try
        {
            var text = $"{SelectedNote.Title}\n{SelectedNote.Body}";
            var related = await _aiService.GetRelatedAsync(SelectedNote.Id, text);

            RelatedNotes.Clear();
            foreach (var item in related)
            {
                RelatedNotes.Add(item);
            }

            HasRelated = RelatedNotes.Count > 0;
            AiStatus = HasRelated
                ? $"Found {RelatedNotes.Count} related note(s)."
                : "No closely related notes found yet.";
        }
        catch (Exception ex)
        {
            AiStatus = DescribeAiError(ex);
        }
    }

    // Opens a related note in the editor when its link is tapped.
    [RelayCommand]
    private async Task OpenRelatedAsync(RelatedNote? related)
    {
        if (related is null)
        {
            return;
        }

        // Prefer the already-loaded instance; otherwise fetch it from the local db.
        var note = Notes.FirstOrDefault(n => n.Id == related.Id)
            ?? await _db.Notes.FirstOrDefaultAsync(n => n.Id == related.Id);

        if (note is not null)
        {
            SelectedNote = note;
        }
    }

    private static string DescribeAiError(Exception ex) => ex switch
    {
        HttpRequestException => "AI failed - is the API reachable?",
        TaskCanceledException => "AI timed out - is Ollama running?",
        _ => $"AI failed - {ex.Message}"
    };
}
