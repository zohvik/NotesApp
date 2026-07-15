# Running NotesApp on Windows

Everything is one codebase — the client's `.csproj` picks the Windows target
automatically when built on Windows. These steps take a fresh Windows 11 PC to
a fully working app (editor, folders, favorites, sync, and local AI).

## 1. Prerequisites (one-time)

1. **.NET 10 SDK** — https://dotnet.microsoft.com/download/dotnet/10.0
2. **MAUI workload** (in a terminal):
   ```powershell
   dotnet workload install maui
   ```
3. **SQL Server LocalDB** — ships with Visual Studio; or install standalone
   via the SQL Server Express installer (choose "LocalDB"). The API's
   `appsettings.Development.json` already points at it, and migrations apply
   automatically at startup — no database setup needed.
4. **Ollama** (for the AI features) — https://ollama.com/download/windows, then:
   ```powershell
   ollama pull llama3.2
   ollama pull nomic-embed-text
   ```
5. **HTTPS dev certificate** (the client talks to the API over https):
   ```powershell
   dotnet dev-certs https --trust
   ```

## 2. Get the code

```powershell
git clone https://github.com/zohvik/NotesApp.git
cd NotesApp
```

## 3. Run it

Terminal 1 — the API (creates/updates the LocalDB database on first run):
```powershell
dotnet run --project src/NotesApp.Api --launch-profile https
```

Terminal 2 — the client:
```powershell
dotnet build src/NotesApp.Client/NotesApp.Client.csproj -t:Run -f net10.0-windows10.0.19041.0
```

Keyboard shortcuts use **Ctrl** on Windows (Ctrl+Z undo, Ctrl+Shift+Z /
Ctrl+Y redo, Ctrl+B/I/U formatting). Everything else — themes, auto-save,
folders, favorites, the AI panel — works identically to macOS.

## Known limitations on Windows (by design, for now)

- **Sync is per-machine.** The PC's API stores notes in its own LocalDB, and
  the Mac's in its Docker SQL Server — they are separate databases. Notes do
  NOT flow between the two machines yet; that needs the API hosted somewhere
  both can reach (the future cloud-deployment phase, which also needs auth).
- **AI needs Ollama running locally** (`ollama serve` starts automatically as
  a Windows service after install).
- The GitHub Actions Windows build (`.github/workflows/windows-build.yml`)
  verifies compilation on every push once the account's billing lock is
  cleared at github.com/settings/billing.
