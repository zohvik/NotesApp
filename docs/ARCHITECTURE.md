# NotesApp — Architecture & Learning Guide

This document has two goals:
1. **Map this codebase** so you can find where anything lives and add features.
2. **Teach the transferable patterns** so you can build *your own* apps in this shape.

Read it top to bottom once; after that, use it as a reference.

---

## Part 1 — The mental model

### One sentence
It's an **offline-first notes app**: you edit notes locally (works with no
internet), a **sync service** pushes/pulls them to a **cloud API**, and an **AI
service** sends note text to a **local AI model** and writes the result back.

### The spine — how data flows
Hold this picture in your head and everything else falls into place:

```
You type  →  local SQLite (on your Mac)  →  SyncService  →  Cloud API  →  SQL Server
                     ↑                                                        │
                     └──────────────  pull newer changes back  ──────────────┘

AI:  note text  →  Cloud API  →  Ollama (local model)  →  result  →  back into the note
```

Two independent loops:
- **Sync loop** — keep every device's copy in agreement.
- **AI loop** — turn text into summaries / drafts / tables / related-notes.

### Why it's split this way (the decisions, and the reasons)
| Decision | Why |
|---|---|
| **Offline-first** (local DB is the source of truth for editing) | The app must work in a lecture with no wifi; the network is an enhancement, never a requirement. |
| **Client + separate API** | Multiple devices need one shared canonical copy; the API owns it. The client never touches the server's DB directly — only via HTTP. |
| **Shared `Core` project** | Both halves must agree on what a "Note" is. One definition, referenced by both, so they can't drift. |
| **AI behind the API** (client → API → Ollama) | Keeps the model connection in one place; the client stays a thin UI. When the API moves to the cloud, only the API's config changes. |
| **Local AI (Ollama)** | No API keys, no per-token cost, runs fully offline. Trade-off: it only works while the API and Ollama sit on the same machine. |

**The transferable lesson:** almost every app is *UI → logic → data*, and most
useful apps are *client ↔ server ↔ database*. Learn this shape once and you can
rebuild it for a budgeting app, a workout tracker, anything.

---

## Part 2 — The four projects

```
NotesApp.slnx                     the solution (ties the projects together)
├── src/NotesApp.Core/            shared data models (the vocabulary)
├── src/NotesApp.Api/             the cloud brain: endpoints + database + AI
├── src/NotesApp.Client/          the MAUI app you see and touch
└── tests/NotesApp.Tests/         automated checks (currently a placeholder)
```

- **Core** references nothing. It's just the nouns (`Note`, `Folder`).
- **Api** and **Client** both reference **Core** — that's how a `Note`
  serialized to JSON on one side deserializes on the other.
- The **four-project split is a design choice**, not something a tool generated.
  A generator gives you one project; deciding on four with these
  responsibilities is *architecture* — the muscle worth building.

---

## Part 3 — The files that matter

### Tier 1: the six files that ARE the app
Understand these and you understand everything. Read in this order:

1. **[Note.cs](../src/NotesApp.Core/Models/Note.cs)** — the core data shape.
   Every feature is "do something with a Note." Note which fields **sync**
   (`Title`, `Body`, `FolderId`, `Tags`, `IsFavorite`) vs. which are **UI-only**
   (`IsActiveTab`, `TitleDisplay`, marked `[JsonIgnore]`/`[NotMapped]`). A
   data-storing feature *starts here*.

2. **[Program.cs](../src/NotesApp.Api/Program.cs)** — the *entire* server in one
   file. Every `app.MapGet/MapPut/MapPost(...)` is one thing the client can ask
   the server to do. This is the complete client↔server contract.

3. **[NotesViewModel.cs](../src/NotesApp.Client/ViewModels/NotesViewModel.cs)** —
   the brain (~900 lines). Every button, command, auto-save, folder filter, tab,
   and AI action lives here. You'll spend most of your time here. It's big —
   don't read top to bottom; jump to the `[RelayCommand]` for the action you
   care about.

4. **[MainPage.xaml](../src/NotesApp.Client/MainPage.xaml)** — the whole UI:
   3-pane layout, tab strip, AI panel, sidebar. Every control binds to a
   property or command in the ViewModel.

5. **[editor/index.html](../src/NotesApp.Client/Resources/Raw/editor/index.html)**
   — the rich editor (~1500 lines HTML/CSS/JS). A *separate little web app* in a
   WebView: slash menu, block menu, drag, colors, undo. It talks to C# through a
   message bridge, never directly.

6. **[MainPage.xaml.cs](../src/NotesApp.Client/MainPage.xaml.cs)** — the
   **bridge** between #5 and the rest. The trickiest file: C#↔JS messages are
   base64-JSON, polled every 100ms. When editor↔app communication breaks, it's
   here.

### Tier 2: the supporting cast (open only when relevant)
| File | Does | Open it when… |
|---|---|---|
| [Folder.cs](../src/NotesApp.Core/Models/Folder.cs) | Folder data shape | adding folder fields |
| [SyncService.cs](../src/NotesApp.Client/Sync/SyncService.cs) | push/pull notes+folders | a new field must sync between devices |
| [AiService.cs](../src/NotesApp.Client/Ai/AiService.cs) | client → API AI calls | adding an AI feature (client side) |
| [OllamaAiService.cs](../src/NotesApp.Api/Services/OllamaAiService.cs) | API → Ollama prompts | changing how the AI is prompted |
| [NoteEmbeddingService.cs](../src/NotesApp.Api/Services/NoteEmbeddingService.cs) | related-notes via embeddings | touching "Find Related" |
| [ThemeManager.cs](../src/NotesApp.Client/Theming/ThemeManager.cs) | color themes | adding/editing a theme |
| [MauiProgram.cs](../src/NotesApp.Client/MauiProgram.cs) | DI wiring (what depends on what) | adding a new service |
| [MarkdownConverter.cs](../src/NotesApp.Client/Text/MarkdownConverter.cs) | AI markdown → editor HTML | AI output renders wrong |
| [Client NotesDbContext.cs](../src/NotesApp.Client/Data/NotesDbContext.cs) / [API NotesDbContext.cs](../src/NotesApp.Api/Data/NotesDbContext.cs) | DB access (SQLite / SQL Server) | adding a table |
| [App.xaml.cs](../src/NotesApp.Client/App.xaml.cs) | app lifecycle, on-close save flush | startup/shutdown behavior |

---

## Part 4 — The golden path: trace one note end to end

The single most useful exercise. Follow "I typed in a note and it saved":

1. **[MainPage.xaml](../src/NotesApp.Client/MainPage.xaml)** — the WebView editor
   (`HybridWebView`) captures your keystrokes.
2. **[editor/index.html](../src/NotesApp.Client/Resources/Raw/editor/index.html)**
   — JS debounces the change and queues a `changed` message.
3. **[MainPage.xaml.cs](../src/NotesApp.Client/MainPage.xaml.cs)** — the 100ms
   poll drains that message and calls `OnEditorContentChanged`.
4. **[NotesViewModel.cs](../src/NotesApp.Client/ViewModels/NotesViewModel.cs)** —
   `EditBody` updates → auto-save schedules → ~0.8s later `SaveChangesAsync()`
   writes to **local SQLite**. (No network yet — offline-first.)
5. Later, **[SyncService.cs](../src/NotesApp.Client/Sync/SyncService.cs)** —
   `PUT /api/notes/{id}` sends the note up.
6. **[Program.cs](../src/NotesApp.Api/Program.cs)** — the endpoint upserts it into
   **SQL Server** (last-write-wins by `UpdatedAt`).

That shape — **UI → bridge → ViewModel → local DB → sync → API → server DB** — is
how nearly every feature flows. Learn to walk it without help and you can read
this codebase.

---

## Part 5 — The concepts you're actually learning (transferable)

These generalize to *any* app you build. Memorize the **concepts**, look up the
**syntax**.

- **MVVM** (Model-View-ViewModel). The UI ([MainPage.xaml](../src/NotesApp.Client/MainPage.xaml))
  holds *no logic*; the ViewModel holds *no UI controls*; **data binding** glues
  them (`{Binding EditTitle}` ↔ the `EditTitle` property; buttons → `Command`s).
  *Generalizes to:* any UI framework with binding (WPF, SwiftUI, React with
  hooks are all variations on this separation).

- **Dependency Injection.** You register services once
  ([MauiProgram.cs](../src/NotesApp.Client/MauiProgram.cs),
  [Program.cs](../src/NotesApp.Api/Program.cs)); the framework builds and hands
  them to whatever asks in its constructor. That's why nothing does
  `new SyncService(...)` by hand. *Generalizes to:* every serious backend
  framework (Spring, ASP.NET, NestJS) works this way.

- **ORM (EF Core).** Write `_db.Notes.Where(...)` in C#; it becomes SQL. The same
  model runs on **SQLite** (client) and **SQL Server** (API). **Migrations** are
  versioned schema changes. *Generalizes to:* Prisma, ActiveRecord, SQLAlchemy —
  same idea.

- **Minimal API / REST endpoints.** Each `app.Map...("/api/...")` is a URL. The
  client calls URLs and passes **JSON**; the server never exposes its DB. This is
  *the* client↔server pattern of the web.

- **HTTP + JSON serialization.** Objects → JSON → sent → objects again. Works
  because both sides share `Core`. *This is how basically all app↔server comms
  work.*

- **Offline-first sync.** Edit locally, reconcile later; conflicts resolved by
  **last-write-wins on `UpdatedAt`**. Simple and good enough until true
  multi-device conflict editing. *A real distributed-systems concept in
  miniature.*

- **Local AI via HTTP.** Ollama is just an HTTP server running a model
  ([OllamaAiService.cs](../src/NotesApp.Api/Services/OllamaAiService.cs)). "Related
  notes" uses **embeddings + cosine similarity**
  ([NoteEmbeddingService.cs](../src/NotesApp.Api/Services/NoteEmbeddingService.cs))
  — turn text into vectors, compare closeness. *Generalizes to any LLM API.*

- **WebView ↔ native bridge.** When a native control can't do what you need (a
  true rich-text editor), you host a **web page** and pass **messages** across the
  boundary ([index.html](../src/NotesApp.Client/Resources/Raw/editor/index.html)
  ↔ [MainPage.xaml.cs](../src/NotesApp.Client/MainPage.xaml.cs)). *This is the
  most advanced/fiddly pattern here — don't judge your understanding by it.*

---

## Part 6 — How to add a feature (the change surface)

The professional skill: **find the nearest existing feature and copy its shape.**
For common additions, here is *every* file you'd touch, in order.

**Add a field to notes** (e.g. a color, a due date):
[Note.cs](../src/NotesApp.Core/Models/Note.cs) → `PatchSchemaAsync` in
[NotesViewModel.cs](../src/NotesApp.Client/ViewModels/NotesViewModel.cs) (add the
`ALTER TABLE`) → EF migration for the API → the upsert copy-block in
[Program.cs](../src/NotesApp.Api/Program.cs) → the pull copy-block in
[SyncService.cs](../src/NotesApp.Client/Sync/SyncService.cs) → UI in
[MainPage.xaml](../src/NotesApp.Client/MainPage.xaml).
⚠️ Miss the upsert/sync copy and it silently won't sync — that exact bug bit
this project twice. **`IsFavorite` is your perfect template:** search the repo
(`Cmd+Shift+F`) for `IsFavorite` and every hit is a spot on the change surface.

**Add a button/action** (e.g. "Duplicate note"):
a `[RelayCommand]` in [NotesViewModel.cs](../src/NotesApp.Client/ViewModels/NotesViewModel.cs)
→ a `<Button Command="...">` in [MainPage.xaml](../src/NotesApp.Client/MainPage.xaml).

**Add an AI feature** (e.g. "translate note"):
prompt method in [OllamaAiService.cs](../src/NotesApp.Api/Services/OllamaAiService.cs)
→ endpoint in [Program.cs](../src/NotesApp.Api/Program.cs)
→ call method in [AiService.cs](../src/NotesApp.Client/Ai/AiService.cs)
→ command in [NotesViewModel.cs](../src/NotesApp.Client/ViewModels/NotesViewModel.cs)
→ button in [MainPage.xaml](../src/NotesApp.Client/MainPage.xaml).

**Add an editor block / formatting** (e.g. an image block, a new highlight):
all inside [editor/index.html](../src/NotesApp.Client/Resources/Raw/editor/index.html)
— CSS for the look, a `slashItems` entry or toolbar button, and make sure
`sanitize()` doesn't strip your new element.

### The review method (before writing any code)
1. Name the nearest existing feature (its "twin").
2. Search the repo for a field/command name from that twin.
3. Each hit is probably a file you must edit — **write that list down first**.
4. That list *is* your plan. Working in this order stops you forgetting the
   silent ones (the upsert, the sync copy).

### Navigation tools (use constantly, don't scroll)
- **F12 / Cmd+Click** — Go to definition ("what is this?")
- **Shift+F12** — Find all references ("who calls this?" = the change surface)
- **Cmd+Shift+F** — full-text search the repo (your most-used tool)
- **Cmd+T** — jump to any symbol by name
- **`git log` / `git blame`** — "*why* does this exist?"

---

## Part 7 — How to recreate this from scratch

To build your *own* app in this shape, this is the order. Notice how little is
"knowledge" — most is running generators and wiring.

### One-time tooling
```bash
dotnet workload install maui          # cross-platform app templates + tooling
dotnet tool install --global dotnet-ef # database migrations CLI
```

### Scaffold the solution and projects (generators do the work)
```bash
dotnet new sln     -n MyApp --format slnx
dotnet new classlib -o src/MyApp.Core       # shared models
dotnet new webapi   -o src/MyApp.Api        # the server
dotnet new maui     -o src/MyApp.Client     # the app
dotnet new xunit    -o tests/MyApp.Tests    # tests
```

### Wire them together (this is the *architecture* part — your decisions)
```bash
# register each project with the solution
dotnet sln MyApp.slnx add src/MyApp.Core/MyApp.Core.csproj   # (repeat per project)
# who references the shared models
dotnet add src/MyApp.Api    reference src/MyApp.Core
dotnet add src/MyApp.Client reference src/MyApp.Core
dotnet add tests/MyApp.Tests reference src/MyApp.Core
```

### Add the libraries each project needs
```bash
# API
dotnet add src/MyApp.Api package Microsoft.EntityFrameworkCore.SqlServer
dotnet add src/MyApp.Api package Microsoft.EntityFrameworkCore.Design
# Client
dotnet add src/MyApp.Client package CommunityToolkit.Mvvm
dotnet add src/MyApp.Client package Microsoft.EntityFrameworkCore.Sqlite
dotnet add src/MyApp.Client package Microsoft.Extensions.Http
dotnet add src/MyApp.Client package SQLitePCLRaw.bundle_e_sqlite3
```

### Generate the database schema from your models
```bash
dotnet ef migrations add InitialCreate --project src/MyApp.Api
dotnet ef database update              --project src/MyApp.Api
```

### Environment (makes it run — not files in the repo)
```bash
dotnet dev-certs https --trust                       # local HTTPS
dotnet user-secrets init --project src/MyApp.Api     # keep the DB password out of git
docker run -e "ACCEPT_EULA=Y" -e "MSSQL_SA_PASSWORD=..." \
  -p 1433:1433 --name myapp-sql -d mcr.microsoft.com/mssql/server:2022-latest
```

### Then build the app, in this order
1. **Models** in Core (your nouns).
2. **DbContext + migration** so they persist.
3. **API endpoints** (GET all / PUT upsert per model) — test with `curl`.
4. **Client DbContext + a ViewModel + a page** that shows local data.
5. **SyncService** to push/pull.
6. Features on top (AI, editor, themes…).

Build **one vertical slice** (one model, end to end) before adding a second —
proving the whole pipeline once is worth more than lots of half-features.

*(See also [WINDOWS.md](WINDOWS.md) for running the finished app on a PC.)*

---

## Part 8 — Honest weak spots (what you'd do differently at scale)

Knowing the limits is part of understanding the design:

- **No API authentication.** Fine while it binds `localhost`; the *first* thing
  to add before any cloud deployment. Right now anyone who can reach the API can
  read/write every note.
- **Client uses `EnsureCreated` + manual `ALTER TABLE`, not migrations.** Simple,
  but every new column needs a hand-written patch (`PatchSchemaAsync`). Real EF
  migrations on the client would be cleaner.
- **Sync is naive last-write-wins.** No per-field merge; the newer whole-note
  wins. Fine for one user on a few devices, not for collaboration.
- **The WebView bridge is polled**, not event-driven — a pragmatic workaround
  because the native HybridWebView JS bridge didn't inject reliably on .NET 10
  Mac Catalyst. It works, but it's the least "textbook" part of the app.
- **Note bodies are one HTML blob**, not addressable blocks — which is why
  Notion-style *synced blocks* and *comments* aren't feasible without a storage
  redesign.

None of these are bugs — they're deliberate "good enough for now" trade-offs.
Recognizing which corners were cut, and why, is exactly the judgment that lets
you design your own apps.
