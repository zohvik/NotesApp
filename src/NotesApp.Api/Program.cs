using Microsoft.EntityFrameworkCore;
using NotesApp.Api.Data;
using NotesApp.Api.Services;
using NotesApp.Core.Models;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

builder.Services.AddDbContext<NotesDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("NotesDb")));

// Typed HttpClient pointing at the local Ollama server. Configurable so we can
// point it elsewhere once the API is no longer co-located with Ollama.
builder.Services.AddHttpClient<OllamaAiService>(client =>
    client.BaseAddress = new Uri(
        builder.Configuration["Ollama:BaseUrl"] ?? "http://localhost:11434"));

builder.Services.AddScoped<NoteEmbeddingService>();

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

// Folders sync the same way notes do: return all (including deleted) so clients
// can mirror deletions, and upsert with last-write-wins by UpdatedAt.
app.MapGet("/api/folders", async (NotesDbContext db) =>
    await db.Folders
        .OrderBy(f => f.Name)
        .ToListAsync())
    .WithName("GetFolders");

app.MapPut("/api/folders/{id}", async (Guid id, Folder incoming, NotesDbContext db) =>
{
    var existing = await db.Folders.FindAsync(id);

    if (existing is null)
    {
        incoming.Id = id;
        db.Folders.Add(incoming);
    }
    else if (incoming.UpdatedAt > existing.UpdatedAt)
    {
        existing.Name = incoming.Name;
        existing.CreatedAt = incoming.CreatedAt;
        existing.UpdatedAt = incoming.UpdatedAt;
        existing.IsDeleted = incoming.IsDeleted;
    }

    await db.SaveChangesAsync();
    return Results.Ok(existing ?? incoming);
})
.WithName("UpsertFolder");

// Sends a note's text to the local Ollama model and returns a short summary.
app.MapPost("/api/ai/summarize", async (SummarizeRequest request, OllamaAiService ai) =>
{
    if (string.IsNullOrWhiteSpace(request.Text))
    {
        return Results.BadRequest("Nothing to summarize.");
    }

    var summary = await ai.SummarizeAsync(request.Text);
    return Results.Ok(new SummarizeResponse(summary));
})
.WithName("Summarize");

// Drafts a brand-new note (title + body) from a short instruction.
app.MapPost("/api/ai/draft", async (DraftRequest request, OllamaAiService ai) =>
{
    if (string.IsNullOrWhiteSpace(request.Prompt))
    {
        return Results.BadRequest("Prompt is required.");
    }

    var draft = await ai.DraftNoteAsync(request.Prompt);
    return Results.Ok(new DraftResponse(draft.Title, draft.Body));
})
.WithName("DraftNote");

// Rewrites existing note text per an instruction (edit, tidy, or "make a table").
app.MapPost("/api/ai/rewrite", async (RewriteRequest request, OllamaAiService ai) =>
{
    if (string.IsNullOrWhiteSpace(request.Text) || string.IsNullOrWhiteSpace(request.Instruction))
    {
        return Results.BadRequest("Text and instruction are both required.");
    }

    var result = await ai.RewriteAsync(request.Text, request.Instruction);
    return Results.Ok(new RewriteResponse(result));
})
.WithName("RewriteNote");

// Finds notes semantically related to the given text (used to link related notes).
app.MapPost("/api/ai/related", async (RelatedRequest request, NoteEmbeddingService embeddings) =>
{
    var related = await embeddings.FindRelatedAsync(request.NoteId, request.Text);
    return Results.Ok(related);
})
.WithName("RelatedNotes");

app.Run();

// Request/response shapes for the AI endpoints.
record SummarizeRequest(string Text);
record SummarizeResponse(string Summary);
record DraftRequest(string Prompt);
record DraftResponse(string Title, string Body);
record RewriteRequest(string Text, string Instruction);
record RewriteResponse(string Result);
record RelatedRequest(Guid NoteId, string Text);
