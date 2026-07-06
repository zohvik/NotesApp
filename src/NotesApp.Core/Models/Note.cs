namespace NotesApp.Core.Models;

public class Note
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Title { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    // Which folder this note lives in. Null means "unfiled".
    public Guid? FolderId { get; set; }

    // Comma-separated tags (e.g. "meeting,q3,planning"). Stored as one string to
    // keep sync simple; the AI can suggest these under "smart organization".
    public string Tags { get; set; } = string.Empty;

    // Cached semantic embedding of the note, stored as a JSON array of floats.
    // Populated lazily by the AI embedding step and used to find related notes.
    public string? Embedding { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public bool IsDeleted { get; set; }
}
