namespace NotesApp.Core.Models;

// A folder groups notes. Kept flat for now (no nesting); a ParentId could be
// added later for a tree. Carries the same sync-friendly fields as Note
// (Guid id, timestamps, soft-delete) so it can ride the same push/pull sync.
public class Folder
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public bool IsDeleted { get; set; }
}
