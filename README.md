# NotesApp

A cross-platform, offline-first note-taking app with a Notion-style block editor
and a **local** AI assistant — no API keys, no cloud LLM, no per-token cost.

Built with .NET 10 / MAUI (macOS + Windows), an ASP.NET Core API, and
[Ollama](https://ollama.com) running language models on your own machine.

<!-- Add screenshots here, e.g.:
![The editor](docs/images/editor.png)
![AI assistant](docs/images/ai-panel.png)
-->

## Features

**Notes & organization**
- Rich block editor: headings, bulleted/numbered lists, to-dos, toggles,
  callouts, quotes, code blocks, tables, dividers, and multi-column layouts
- Notion-style interactions — `/` slash menu, a drag-to-reorder block handle
  with a right-click block menu, `Tab`/`Shift+Tab` outline nesting, and a
  floating toolbar with text colors and highlights
- Auto-save while you type, undo/redo, folders, tags, favorites, note tabs,
  and five themes (including Catppuccin)

**Offline-first sync**
- Every edit writes to a **local SQLite** database first, so the app works with
  no network at all
- A background sync service pushes and pulls to the API, reconciling with
  last-write-wins on `UpdatedAt`

**Local AI (via Ollama)**
- Summarize a note, draft a new one from a prompt, rewrite or restructure text,
  and convert prose into tables
- Per-block "Ask AI" from the block menu
- Inline ghost-text autocomplete as you type
- **Related notes** through vector embeddings and cosine similarity

## Architecture

```
You type  →  local SQLite (per device)  →  SyncService  →  API  →  SQL Server
                    ↑                                                  │
                    └────────────  pull newer changes back  ───────────┘

AI:  note text  →  API  →  Ollama (local model)  →  result  →  back into the note
```

| Project | Role |
|---|---|
| `src/NotesApp.Core` | Shared models (`Note`, `Folder`) + pure text utilities |
| `src/NotesApp.Api` | ASP.NET Core Minimal API, EF Core → SQL Server, Ollama integration |
| `src/NotesApp.Client` | .NET MAUI app (MVVM) with local SQLite and a WebView editor |
| `tests/NotesApp.Tests` | xUnit tests |

The rich editor is an HTML/CSS/JS document hosted in a `HybridWebView`, talking
to C# over a JSON message bridge — the native control couldn't provide real
block editing.

A full walkthrough lives in **[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)**,
including a file-by-file guide and a golden-path trace of a single note.

## Tech stack

.NET 10 · MAUI · ASP.NET Core Minimal APIs · EF Core (SQLite + SQL Server) ·
CommunityToolkit.Mvvm · Docker · Ollama (`llama3.2`, `nomic-embed-text`) · xUnit

## Getting started

**Prerequisites:** [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0),
the MAUI workload (`dotnet workload install maui`), Docker, and
[Ollama](https://ollama.com).

```bash
# 1. Models for the AI features
ollama pull llama3.2
ollama pull nomic-embed-text

# 2. SQL Server for the API
docker run -e "ACCEPT_EULA=Y" -e "MSSQL_SA_PASSWORD=<your-password>" \
  -p 1433:1433 --name notesapp-sql -d mcr.microsoft.com/mssql/server:2022-latest

# 3. Point the API at it (kept out of git via user-secrets)
dotnet user-secrets set "ConnectionStrings:NotesDb" \
  "Server=localhost,1433;Database=NotesApp;User Id=sa;Password=<your-password>;TrustServerCertificate=True" \
  --project src/NotesApp.Api

# 4. Trust the local HTTPS certificate
dotnet dev-certs https --trust
```

Then run the API and the client in two terminals:

```bash
dotnet run --project src/NotesApp.Api --launch-profile https
dotnet build src/NotesApp.Client/NotesApp.Client.csproj -t:Run -f net10.0-maccatalyst
```

The API applies its database migrations automatically on first run.
For Windows, see **[docs/WINDOWS.md](docs/WINDOWS.md)**.

```bash
dotnet test    # run the test suite
```

## Project status & known limitations

This is a working personal project, developed openly — the trade-offs are
deliberate and documented rather than hidden:

- **The API has no authentication.** Fine while it binds `localhost`; it is the
  required next step before any real deployment.
- **Sync is last-write-wins** on whole notes, not per-field merge — enough for
  one person across devices, not for live collaboration.
- **The AI requires Ollama on the same machine as the API**, so AI features stop
  working if the API is hosted remotely without a model beside it.
- **Note bodies are stored as a single HTML document**, not addressable blocks,
  which is why Notion-style synced blocks and comments aren't feasible yet.

Planned: meeting transcription (Whisper) and Outlook integration via Microsoft
Graph.

## License

No license has been chosen yet, so all rights are reserved by default.
