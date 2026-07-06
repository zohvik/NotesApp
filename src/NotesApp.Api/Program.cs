using Microsoft.EntityFrameworkCore;
using NotesApp.Api.Data;
using NotesApp.Core.Models;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

builder.Services.AddDbContext<NotesDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("NotesDb")));

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

// Returns every note, including deleted ones, so clients can mirror deletions during sync.
app.MapGet("/api/notes", async (NotesDbContext db) =>
    await db.Notes
        .OrderByDescending(n => n.UpdatedAt)
        .ToListAsync())
    .WithName("GetNotes");

// Upsert: creates the note if it doesn't exist yet, otherwise applies the
// incoming version only if it's newer than what's stored (last-write-wins).
app.MapPut("/api/notes/{id}", async (Guid id, Note incoming, NotesDbContext db) =>
{
    var existing = await db.Notes.FindAsync(id);

    if (existing is null)
    {
        incoming.Id = id;
        db.Notes.Add(incoming);
    }
    else if (incoming.UpdatedAt > existing.UpdatedAt)
    {
        existing.Title = incoming.Title;
        existing.Body = incoming.Body;
        existing.CreatedAt = incoming.CreatedAt;
        existing.UpdatedAt = incoming.UpdatedAt;
        existing.IsDeleted = incoming.IsDeleted;
    }

    await db.SaveChangesAsync();
    return Results.Ok(existing ?? incoming);
})
.WithName("UpsertNote");

app.Run();
