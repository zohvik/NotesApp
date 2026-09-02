using System.ComponentModel;
using NotesApp.Core.Models;

namespace NotesApp.Tests;

// Note carries UI-facing behavior (a display fallback and change notifications)
// that the note list depends on, so it's worth pinning down.
public class NoteTests
{
    [Fact]
    public void NewNote_HasIdAndEmptyDefaults()
    {
        var note = new Note();

        Assert.NotEqual(Guid.Empty, note.Id);
        Assert.Equal(string.Empty, note.Title);
        Assert.Equal(string.Empty, note.Body);
        Assert.False(note.IsDeleted);
        Assert.False(note.IsFavorite);
        Assert.Null(note.FolderId); // unfiled by default
    }

    // Auto-save allows untitled notes, so the sidebar needs a readable stand-in.
    [Theory]
    [InlineData("", "Untitled")]
    [InlineData("   ", "Untitled")]
    [InlineData("Meeting notes", "Meeting notes")]
    public void TitleDisplay_FallsBackWhenBlank(string title, string expected)
    {
        var note = new Note { Title = title };

        Assert.Equal(expected, note.TitleDisplay);
    }

    // The note list binds to TitleDisplay, so renaming must notify both names
    // or the sidebar would keep showing the old title until a reload.
    [Fact]
    public void SettingTitle_NotifiesTitleAndTitleDisplay()
    {
        var note = new Note();
        var raised = new List<string?>();
        ((INotifyPropertyChanged)note).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        note.Title = "Renamed";

        Assert.Contains(nameof(Note.Title), raised);
        Assert.Contains(nameof(Note.TitleDisplay), raised);
    }

    [Fact]
    public void SettingSameValue_DoesNotNotify()
    {
        var note = new Note { Title = "Same" };
        var raised = 0;
        ((INotifyPropertyChanged)note).PropertyChanged += (_, _) => raised++;

        note.Title = "Same";

        Assert.Equal(0, raised);
    }

    [Fact]
    public void TogglingFavorite_Notifies()
    {
        var note = new Note();
        var raised = new List<string?>();
        ((INotifyPropertyChanged)note).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        note.IsFavorite = true;

        Assert.Contains(nameof(Note.IsFavorite), raised);
    }
}
