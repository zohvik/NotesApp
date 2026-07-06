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

app.MapGet("/api/notes", async (NotesDbContext db) =>
    await db.Notes
        .Where(n => !n.IsDeleted)
        .OrderByDescending(n => n.UpdatedAt)
        .ToListAsync())
    .WithName("GetNotes");

app.MapPost("/api/notes", async (Note note, NotesDbContext db) =>
{
    db.Notes.Add(note);
    await db.SaveChangesAsync();
    return Results.Created($"/api/notes/{note.Id}", note);
})
.WithName("CreateNote");

app.Run();
