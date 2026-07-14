using System.Collections.ObjectModel;
using System.Net;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using NotesApp.Client.Ai;
using NotesApp.Client.Data;
using NotesApp.Client.Sync;
using NotesApp.Client.Text;
using NotesApp.Client.Theming;
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

    // Whether the floating AI panel is open (hidden by default).
    [ObservableProperty]
    private bool isAiOpen;

    // Theme selection. Changing SelectedTheme repaints the app via ThemeManager.
    public IReadOnlyList<string> Themes => ThemeManager.ThemeNames;

    [ObservableProperty]
    private string selectedTheme = ThemeManager.Current;

    partial void OnSelectedThemeChanged(string value) => ThemeManager.Apply(value);

    [RelayCommand]
    private void ToggleAi() => IsAiOpen = !IsAiOpen;

    // Raised when the WebView editor should be (re)loaded with this HTML.
    // The code-behind subscribes and pushes it into the HybridWebView.
    public event Action<string>? EditorContentRequested;

    // Set by the code-behind: fetches the editor's CURRENT html on demand.
    // The normal 'changed' notifications are debounced (~0.5s behind), so Save
    // uses this to grab the freshest content instead of a stale snapshot.
    public Func<Task<string?>>? EditorContentFetcher { get; set; }

    // Serializes all work on the shared DbContext. EF Core contexts are not
    // safe for concurrent use, and fire-and-forget paths (folder switching,
    // startup sync) could otherwise overlap a query already in flight.
    private readonly SemaphoreSlim _dbLock = new(1, 1);

    private void PushToEditor(string html) => EditorContentRequested?.Invoke(html);

    // Called by the code-behind when the WebView reports an edit. Sets the body
    // directly (no PushToEditor) so we don't bounce the content back and forth.
    public void OnEditorContentChanged(string html) => EditBody = html;

    // The note body is stored as editor HTML. These convert to/from plain text so
    // the AI (which works in text) gets clean input and its text output can be shown.
    private static bool LooksLikeHtml(string s) =>
        !string.IsNullOrEmpty(s) && Regex.IsMatch(s, "<[a-zA-Z/][^>]*>");

    private static string RenderForEditor(string body) =>
        LooksLikeHtml(body) ? body : TextToHtml(body);

    private static string TextToHtml(string text) =>
        string.IsNullOrEmpty(text)
            ? string.Empty
            : WebUtility.HtmlEncode(text).Replace("\r\n", "\n").Replace("\n", "<br>");

    private static string HtmlToText(string html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return string.Empty;
        }

        // Cell boundaries become spaces so table content doesn't glue ("John28"),
        // then block boundaries become newlines, strip remaining tags, decode entities.
        var text = Regex.Replace(html, "(?i)</t[dh]>", " ");
        text = Regex.Replace(text, "(?i)<(br|/p|/div|/h[1-6]|/tr|/li)\\s*/?>", "\n");
        text = Regex.Replace(text, "<[^>]+>", string.Empty);
        return WebUtility.HtmlDecode(text).Trim();
    }

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
        PushToEditor(RenderForEditor(EditBody));

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
        await _dbLock.WaitAsync();
        try
        {
            await _db.Database.EnsureCreatedAsync();
            await LoadFoldersCoreAsync();
            await LoadNotesCoreAsync();
        }
        finally
        {
            _dbLock.Release();
        }
    }

    // Locked wrappers around the unlocked cores. The cores never take the lock
    // themselves so callers that already hold it (like LoadDataAsync) can compose
    // them without deadlocking — SemaphoreSlim is not re-entrant.
    private async Task LoadFoldersAsync()
    {
        await _dbLock.WaitAsync();
        try { await LoadFoldersCoreAsync(); }
        finally { _dbLock.Release(); }
    }

    private async Task LoadNotesAsync()
    {
        await _dbLock.WaitAsync();
        try { await LoadNotesCoreAsync(); }
        finally { _dbLock.Release(); }
    }

    private async Task LoadFoldersCoreAsync()
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

    private async Task LoadNotesCoreAsync()
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

        await _dbLock.WaitAsync();
        try
        {
            _db.Folders.Add(folder);
            await _db.SaveChangesAsync();
        }
        finally
        {
            _dbLock.Release();
        }

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

        await _dbLock.WaitAsync();
        try
        {
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
        }
        finally
        {
            _dbLock.Release();
        }

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
        PushToEditor(string.Empty);
    }

    [RelayCommand]
    private async Task SaveNoteAsync()
    {
        if (string.IsNullOrWhiteSpace(EditTitle))
        {
            return;
        }

        // Grab the freshest editor content first: the debounced 'changed' path
        // can lag a save by ~0.5s, which would silently drop the last keystrokes.
        if (EditorContentFetcher is not null)
        {
            var latest = await EditorContentFetcher();
            if (latest is not null)
            {
                EditBody = latest;
            }
        }

        var now = DateTimeOffset.UtcNow;
        Guid savedId;

        await _dbLock.WaitAsync();
        try
        {
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
            await LoadNotesCoreAsync();
        }
        finally
        {
            _dbLock.Release();
        }

        SelectedNote = Notes.FirstOrDefault(n => n.Id == savedId);
    }

    [RelayCommand]
    private async Task DeleteNoteAsync()
    {
        if (SelectedNote is null)
        {
            return;
        }

        await _dbLock.WaitAsync();
        try
        {
            SelectedNote.IsDeleted = true;
            SelectedNote.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync();
        }
        finally
        {
            _dbLock.Release();
        }

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
        var bodyText = HtmlToText(EditBody);
        if (string.IsNullOrWhiteSpace(bodyText))
        {
            AiSummary = "Nothing to summarize yet.";
            return;
        }

        AiSummary = "Summarizing...";
        try
        {
            AiSummary = await _aiService.SummarizeAsync(bodyText);
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
            EditBody = MarkdownConverter.ToHtml(draft.Body);
            PushToEditor(EditBody);
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
            var result = await _aiService.RewriteAsync(HtmlToText(EditBody), AiPrompt);
            EditBody = MarkdownConverter.ToHtml(result);
            PushToEditor(EditBody);
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
            var result = await _aiService.RewriteAsync(
                HtmlToText(EditBody),
                "Convert this into a clean Markdown table. Return only the table.");
            EditBody = MarkdownConverter.ToHtml(result);
            PushToEditor(EditBody);
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
            var text = $"{SelectedNote.Title}\n{HtmlToText(SelectedNote.Body)}";
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
        var note = Notes.FirstOrDefault(n => n.Id == related.Id);
        if (note is null)
        {
            await _dbLock.WaitAsync();
            try { note = await _db.Notes.FirstOrDefaultAsync(n => n.Id == related.Id); }
            finally { _dbLock.Release(); }
        }

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
