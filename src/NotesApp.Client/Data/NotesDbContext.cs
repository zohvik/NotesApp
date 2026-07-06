using Microsoft.EntityFrameworkCore;
using NotesApp.Core.Models;

namespace NotesApp.Client.Data;

public class NotesDbContext : DbContext
{
    public NotesDbContext(DbContextOptions<NotesDbContext> options) : base(options)
    {
    }

    public DbSet<Note> Notes => Set<Note>();

    public DbSet<Folder> Folders => Set<Folder>();
}
