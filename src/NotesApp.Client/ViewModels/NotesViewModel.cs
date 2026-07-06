using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using NotesApp.Client.Data;
using NotesApp.Core.Models;

namespace NotesApp.Client.ViewModels;

public partial class NotesViewModel : ObservableObject
{
    private readonly NotesDbContext _db;

    public ObservableCollection<Note> Notes { get; } = new();

    [ObservableProperty]
    private Note? selectedNote;

    [ObservableProperty]
    private string editTitle = string.Empty;

    [ObservableProperty]
    private string editBody = string.Empty;

    public NotesViewModel(NotesDbContext db)
    {
        _db = db;
    }

    partial void OnSelectedNoteChanged(Note? value)
    {
        EditTitle = value?.Title ?? string.Empty;
        EditBody = value?.Body ?? string.Empty;
    }

    [RelayCommand]
    private async Task LoadNotesAsync()
    {
        await _db.Database.EnsureCreatedAsync();

        var notes = await _db.Notes
            .Where(n => !n.IsDeleted)
            .OrderByDescending(n => n.UpdatedAt)
            .ToListAsync();

        Notes.Clear();
        foreach (var note in notes)
        {
            Notes.Add(note);
        }
    }

    [RelayCommand]
    private void New()
    {
        SelectedNote = null;
        EditTitle = string.Empty;
        EditBody = string.Empty;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(EditTitle))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;

        if (SelectedNote is null)
        {
            var note = new Note
            {
                Title = EditTitle,
                Body = EditBody,
                CreatedAt = now,
                UpdatedAt = now
            };
            _db.Notes.Add(note);
            await _db.SaveChangesAsync();

            Notes.Insert(0, note);
            SelectedNote = note;
        }
        else
        {
            SelectedNote.Title = EditTitle;
            SelectedNote.Body = EditBody;
            SelectedNote.UpdatedAt = now;
            await _db.SaveChangesAsync();
        }
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (SelectedNote is null)
        {
            return;
        }

        SelectedNote.IsDeleted = true;
        SelectedNote.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync();

        Notes.Remove(SelectedNote);
        New();
    }
}
