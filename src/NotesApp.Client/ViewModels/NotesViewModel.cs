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

    // Sentinel "folders" pinned to the top of the sidebar. They're views, not
    // real folders: their fixed ids are never written to the database, they just
    // select a different filter in LoadNotesCoreAsync.
    public static readonly Folder AllNotesFolder = new() { Id = Guid.Empty, Name = "All Notes" };
    public static readonly Folder RecentsFolder = new() { Id = new Guid("00000000-0000-0000-0000-000000000001"), Name = "Recents" };
    public static readonly Folder FavoritesFolder = new() { Id = new Guid("00000000-0000-0000-0000-000000000002"), Name = "Favorites" };

    private const int RecentsCount = 15;

    private static bool IsSentinel(Folder? f) =>
        f is not null && (f.Id == AllNotesFolder.Id || f.Id == RecentsFolder.Id || f.Id == FavoritesFolder.Id);

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

    // Subtle auto-save indicator ("Saved 3:42 PM"), Notion-style.
    [ObservableProperty]
    private string saveStatus = string.Empty;

    // Whether the floating AI panel is open (hidden by default).
    [ObservableProperty]
    private bool isAiOpen;

    // Whether the folder sidebar is expanded (Notion-style collapse). The page
    // code-behind reacts by resizing the sidebar column; remembered across runs.
    [ObservableProperty]
    private bool isSidebarOpen = Preferences.Get("sidebarOpen", true);

    partial void OnIsSidebarOpenChanged(bool value) => Preferences.Set("sidebarOpen", value);

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarOpen = !IsSidebarOpen;

    // Theme selection. Changing SelectedTheme repaints the app via ThemeManager.
    public IReadOnlyList<string> Themes => ThemeManager.ThemeNames;

    [ObservableProperty]
    private string selectedTheme = ThemeManager.Current;

    partial void OnSelectedThemeChanged(string value) => ThemeManager.Apply(value);

    [RelayCommand]
    private void ToggleAi() => IsAiOpen = !IsAiOpen;

    // Raised when the WebView editor should be (re)loaded with this HTML.
    // The code-behind subscribes and pushes it into the HybridWebView.
    // keepHistory=true preserves the editor's undo stack (AI edits to the SAME
    // note stay undoable); false resets it (a different note was loaded).
    public event Action<string, bool>? EditorContentRequested;

    // Set by the code-behind: fetches the editor's CURRENT html on demand.
    // The normal 'changed' notifications are debounced (~0.5s behind), so Save
    // uses this to grab the freshest content instead of a stale snapshot.
    public Func<Task<string?>>? EditorContentFetcher { get; set; }

    // Serializes all work on the shared DbContext. EF Core contexts are not
    // safe for concurrent use, and fire-and-forget paths (folder switching,
    // startup sync) could otherwise overlap a query already in flight.
    private readonly SemaphoreSlim _dbLock = new(1, 1);

    // ---- Auto-save (Notion-style: just type, it persists) ----
    // Editing any field schedules a save ~0.8s after typing stops. Switching
    // notes flushes pending edits immediately so nothing is ever lost.
    private CancellationTokenSource? _autoSaveCts;
    private bool _autoSavePending;
    private bool _loadingNote; // true while a note is being loaded INTO the edit fields
    private bool _bubblingNote; // true while Notes.Move bubbles the saved note to the top

    partial void OnEditTitleChanged(string value) => ScheduleAutoSave();
    partial void OnEditBodyChanged(string value) => ScheduleAutoSave();
    partial void OnEditTagsChanged(string value) => ScheduleAutoSave();

    private void ScheduleAutoSave()
    {
        // Field changes caused by loading a note aren't user edits - skip those.
        if (_loadingNote)
        {
            return;
        }

        _autoSaveCts?.Cancel();
        var cts = _autoSaveCts = new CancellationTokenSource();
        _autoSavePending = true;
        _ = AutoSaveAfterDelayAsync(cts.Token);
    }

    private async Task AutoSaveAfterDelayAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(800, ct);
        }
        catch (TaskCanceledException)
        {
            return; // superseded by more typing or a flush
        }

        await AutoSaveNowAsync(SelectedNote, EditTitle, EditBody, EditTags, selectAfterCreate: true);
    }

    // Saves the given values into the given note (or creates one when target is
    // null and there's anything to keep). Values are passed explicitly so a
    // flush can still save note A's edits after the UI has moved on to note B.
    private async Task AutoSaveNowAsync(Note? target, string title, string body, string tags, bool selectAfterCreate)
    {
        _autoSavePending = false;
        var now = DateTimeOffset.UtcNow;

        if (target is null)
        {
            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(body) && string.IsNullOrWhiteSpace(tags))
            {
                return; // nothing worth creating
            }

            // Notes created inside a sentinel tab (All/Recents/Favorites) are unfiled.
            var folderId = (SelectedFolder is null || IsSentinel(SelectedFolder))
                ? (Guid?)null
                : SelectedFolder.Id;

            var note = new Note
            {
                Title = title,
                Body = body,
                Tags = tags,
                FolderId = folderId,
                CreatedAt = now,
                UpdatedAt = now
            };

            await _dbLock.WaitAsync();
            try
            {
                _db.Notes.Add(note);
                await _db.SaveChangesAsync();
            }
            finally
            {
                _dbLock.Release();
            }

            Notes.Insert(0, note);
            if (selectAfterCreate)
            {
                // Set the backing field directly: going through the property would
                // re-trigger OnSelectedNoteChanged and reload the editor mid-typing.
#pragma warning disable MVVMTK0034 // deliberate bypass of the generated setter (see above)
                selectedNote = note;
#pragma warning restore MVVMTK0034
                OnPropertyChanged(nameof(SelectedNote));
            }
        }
        else
        {
            if (target.Title == title && target.Body == body && target.Tags == tags)
            {
                return; // nothing changed
            }

            await _dbLock.WaitAsync();
            try
            {
                target.Title = title;
                target.Body = body;
                target.Tags = tags;
                target.UpdatedAt = now;
                await _db.SaveChangesAsync();
            }
            finally
            {
                _dbLock.Release();
            }

            // Keep the list sorted by last edited: bubble the note to the top.
            // On Mac, Move preserves the CollectionView selection; on Windows it
            // CLEARS it, which would cascade into blanking the editor mid-edit.
            // So suppress the transient deselection and restore the selection.
            var index = Notes.IndexOf(target);
            if (index > 0)
            {
                _bubblingNote = true;
                try
                {
                    Notes.Move(index, 0);

                    // The clear can be raised synchronously inside Move or via a
                    // queued binding update; yield one dispatcher turn so a
                    // deferred clear also lands while the guard is up.
                    await Task.Yield();

                    // Restore only from null so a REAL selection change the user
                    // made in this window is never fought. Backing-field write:
                    // the property setter would reload the editor mid-typing.
                    if (SelectedNote is null)
                    {
#pragma warning disable MVVMTK0034 // deliberate bypass of the generated setter (see above)
                        selectedNote = target;
#pragma warning restore MVVMTK0034
                        OnPropertyChanged(nameof(SelectedNote));
                    }
                }
                finally
                {
                    _bubblingNote = false;
                }
            }
        }

        SaveStatus = $"Saved {DateTimeOffset.Now:t}";
    }

    // Commits any pending (not-yet-fired) auto-save immediately.
    private async Task FlushAutoSaveAsync(Note? target, string title, string body, string tags)
    {
        _autoSaveCts?.Cancel();
        if (_autoSavePending)
        {
            await AutoSaveNowAsync(target, title, body, tags, selectAfterCreate: false);
        }
    }

    // Full flush used when focus leaves the app (and before sync): grab the very
    // latest editor content, then commit whatever is pending.
    public async Task FlushPendingEditsAsync()
    {
        if (EditorContentFetcher is not null)
        {
            var latest = await EditorContentFetcher();
            if (latest is not null && latest != EditBody)
            {
                EditBody = latest; // marks the auto-save pending...
            }
        }

        await FlushAutoSaveAsync(SelectedNote, EditTitle, EditBody, EditTags); // ...committed here
    }

    // Last-ditch synchronous flush for app shutdown. The window is being torn
    // down, so we can't await (continuations may never run) or reach the editor's
    // JavaScript; save what the ViewModel already has using EF's sync API.
    public void FlushPendingEditsSync()
    {
        _autoSaveCts?.Cancel();
        if (!_autoSavePending || SelectedNote is null)
        {
            return; // new-note creation on shutdown isn't worth the complexity
        }

        _autoSavePending = false;
        if (SelectedNote.Title == EditTitle && SelectedNote.Body == EditBody && SelectedNote.Tags == EditTags)
        {
            return;
        }

        // Bounded wait: if an async save holds the lock right now, its
        // continuation needs this (main) thread - blocking forever would
        // deadlock the quit. After 2s, give up; that in-flight save is the
        // one carrying these edits anyway.
        if (!_dbLock.Wait(TimeSpan.FromSeconds(2)))
        {
            return;
        }

        try
        {
            SelectedNote.Title = EditTitle;
            SelectedNote.Body = EditBody;
            SelectedNote.Tags = EditTags;
            SelectedNote.UpdatedAt = DateTimeOffset.UtcNow;
            _db.SaveChanges();
        }
        finally
        {
            _dbLock.Release();
        }
    }

    private void PushToEditor(string html, bool keepHistory = false) =>
        EditorContentRequested?.Invoke(html, keepHistory);

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

    partial void OnSelectedNoteChanged(Note? oldValue, Note? newValue)
    {
        // WinUI clears the CollectionView selection while a Notes.Move is in
        // flight (auto-save bubbling the note to the top). That deselection is
        // spurious - ignore it, or it would blank the editor mid-edit. The
        // selection is restored right after the Move. Only NULL transitions are
        // swallowed: a real click on another note during the guard window must
        // still be honored.
        if (_bubblingNote && newValue is null)
        {
            return;
        }

        // The edit fields still hold the PREVIOUS note's text here - commit any
        // pending auto-save for it before they get overwritten below.
        _ = FlushAutoSaveAsync(oldValue, EditTitle, EditBody, EditTags);

        _loadingNote = true;
        EditTitle = newValue?.Title ?? string.Empty;
        EditBody = newValue?.Body ?? string.Empty;
        EditTags = newValue?.Tags ?? string.Empty;
        _loadingNote = false;

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
            await PatchSchemaAsync();
            await LoadFoldersCoreAsync();
            await LoadNotesCoreAsync();
        }
        finally
        {
            _dbLock.Release();
        }
    }

    // EnsureCreated only builds brand-new databases; it never alters existing
    // ones, and the client has no EF migrations. So a database created before a
    // column existed (e.g. IsFavorite shipped after this PC's db was made) is
    // missing it and every Notes query would fail. Patch such columns in here.
    private async Task PatchSchemaAsync()
    {
        var hasIsFavorite = (await _db.Database
            .SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM pragma_table_info('Notes') WHERE name = 'IsFavorite'")
            .ToListAsync())[0] > 0;

        if (!hasIsFavorite)
        {
            await _db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE Notes ADD COLUMN IsFavorite INTEGER NOT NULL DEFAULT 0");
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
        Folders.Add(RecentsFolder);
        Folders.Add(FavoritesFolder);
        foreach (var folder in folders)
        {
            Folders.Add(folder);
        }

        SelectedFolder ??= AllNotesFolder;
    }

    private async Task LoadNotesCoreAsync()
    {
        var query = _db.Notes.Where(n => !n.IsDeleted);

        if (SelectedFolder is not null && SelectedFolder.Id == FavoritesFolder.Id)
        {
            query = query.Where(n => n.IsFavorite);
        }
        else if (SelectedFolder is not null && !IsSentinel(SelectedFolder))
        {
            var folderId = SelectedFolder.Id;
            query = query.Where(n => n.FolderId == folderId);
        }
        // All Notes and Recents take everything; Recents just truncates below.

        // SQLite can't ORDER BY DateTimeOffset, so sort in memory after fetching.
        var notes = (await query.ToListAsync())
            .OrderByDescending(n => n.UpdatedAt)
            .AsEnumerable();

        if (SelectedFolder is not null && SelectedFolder.Id == RecentsFolder.Id)
        {
            notes = notes.Take(RecentsCount);
        }

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
        // The sentinel tabs (All Notes / Recents / Favorites) can't be deleted.
        if (SelectedFolder is null || IsSentinel(SelectedFolder))
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
        // Keep whatever was being typed before starting a fresh note.
        _ = FlushAutoSaveAsync(SelectedNote, EditTitle, EditBody, EditTags);

        SelectedNote = null;
        EditTitle = string.Empty;
        EditBody = string.Empty;
        EditTags = string.Empty;
        PushToEditor(string.Empty);
    }

    // Stars/unstars the open note. Saves immediately (no debounce) - a favorite
    // toggle is a deliberate click, not typing.
    [RelayCommand]
    private async Task ToggleFavoriteAsync()
    {
        if (SelectedNote is null)
        {
            return;
        }

        await _dbLock.WaitAsync();
        try
        {
            SelectedNote.IsFavorite = !SelectedNote.IsFavorite;
            SelectedNote.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync();
        }
        finally
        {
            _dbLock.Release();
        }

        SaveStatus = $"Saved {DateTimeOffset.Now:t}";
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
            // Commit the very latest editor state first, so sync never uploads a
            // stale body (the 'changed' pipeline lags typing slightly).
            await FlushPendingEditsAsync();

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
            AiStatus = "Draft ready - it will auto-save.";
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
            PushToEditor(EditBody, keepHistory: true); // same note: Cmd+Z can revert the AI edit
            AiStatus = "Applied.";
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
            PushToEditor(EditBody, keepHistory: true); // same note: Cmd+Z can revert the AI edit
            AiStatus = "Table ready.";
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
