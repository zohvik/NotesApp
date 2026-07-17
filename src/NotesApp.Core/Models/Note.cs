using System.ComponentModel;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace NotesApp.Core.Models;

// Implements INotifyPropertyChanged (a BCL interface, no extra dependency) for
// the properties the note LIST binds to, so auto-saving a title/tags edit
// updates the sidebar immediately without reloading the whole list.
public class Note : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private string _title = string.Empty;
    private string _tags = string.Empty;
    private bool _isFavorite;
    private bool _isActiveTab;

    public Guid Id { get; set; } = Guid.NewGuid();

    public string Title
    {
        get => _title;
        set
        {
            if (_title == value)
            {
                return;
            }

            _title = value;
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(TitleDisplay));
        }
    }

    // What the note list shows. Auto-save allows untitled notes, so an empty
    // title needs a readable stand-in. Not stored or synced - display only.
    [NotMapped]
    [JsonIgnore]
    public string TitleDisplay => string.IsNullOrWhiteSpace(Title) ? "Untitled" : Title;

    public string Body { get; set; } = string.Empty;

    // Which folder this note lives in. Null means "unfiled".
    public Guid? FolderId { get; set; }

    // Comma-separated tags (e.g. "meeting,q3,planning"). Stored as one string to
    // keep sync simple; the AI can suggest these under "smart organization".
    public string Tags
    {
        get => _tags;
        set
        {
            if (_tags == value)
            {
                return;
            }

            _tags = value;
            OnPropertyChanged(nameof(Tags));
        }
    }

    // Whether this note is the active tab in the editor's tab strip.
    // Pure UI state - never stored or synced.
    [NotMapped]
    [JsonIgnore]
    public bool IsActiveTab
    {
        get => _isActiveTab;
        set
        {
            if (_isActiveTab == value)
            {
                return;
            }

            _isActiveTab = value;
            OnPropertyChanged(nameof(IsActiveTab));
        }
    }

    // Starred by the user; shown in the sidebar's Favorites tab. Notifies so the
    // star button and Favorites list react immediately when toggled.
    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (_isFavorite == value)
            {
                return;
            }

            _isFavorite = value;
            OnPropertyChanged(nameof(IsFavorite));
        }
    }

    // Cached semantic embedding of the note, stored as a JSON array of floats.
    // Populated lazily by the AI embedding step and used to find related notes.
    // Server-side only: JsonIgnore keeps these large vectors out of every sync
    // payload — the API computes and stores them, clients never use them.
    [JsonIgnore]
    public string? Embedding { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public bool IsDeleted { get; set; }

    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
