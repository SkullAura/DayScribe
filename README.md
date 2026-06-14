# NotesMuchachos

Native Windows notes app with registration, voice/audio transcription, photo attachments, day/hour calendar, offline-first SQLite storage, and a sync backend.

## Projects

- `src/ProjectCal.Client` - WinUI 3 Windows client.
- `src/ProjectCal.Api` - ASP.NET Core API for auth, notes, media upload, sync, and diagnostics.
- `src/ProjectCal.Worker` - optional background transcription worker for local/VPS deployments.
- `src/ProjectCal.Shared` - shared DTOs and enums.
- `tests/ProjectCal.Tests` - API integration tests.

## Local Development

Install .NET 10 SDK, then run:

```powershell
dotnet restore ProjectCal.slnx
dotnet build ProjectCal.slnx
dotnet test ProjectCal.slnx
```

For backend-only development with SQLite:

```powershell
dotnet run --project src\ProjectCal.Api\ProjectCal.Api.csproj --environment Development
```

For PostgreSQL and containerized API/worker:

```powershell
docker compose up --build
```

## Groq Transcription

In the Render Free setup, real audio transcription is handled directly by `src/ProjectCal.Api` after an audio upload. The Windows app never stores or asks the user for the Groq key.

Set a Groq API key on the server before starting the API:

```powershell
$env:GROQ_API_KEY = "gsk-your-groq-api-key"
$env:Groq__TranscriptionModel = "whisper-large-v3-turbo"
dotnet run --project src\ProjectCal.Api\ProjectCal.Api.csproj --environment Development
```

The API sends uploaded audio to Groq's OpenAI-compatible transcription endpoint. Without `GROQ_API_KEY`, uploaded audio is saved but transcription is marked failed.

## Supabase Postgres

NotesMuchachos can use Supabase as its Postgres database. Get the connection string from:

Supabase dashboard -> Project Settings -> Database -> Connection string.

For PowerShell local runs:

```powershell
$env:SUPABASE_DB_CONNECTION_STRING = "Host=...;Port=5432;Database=postgres;Username=postgres....;Password=...;SSL Mode=Require"
$env:Database__Provider = "Postgres"
$env:Jwt__SigningKey = "replace-with-a-long-random-signing-key-at-least-32-bytes"

dotnet run --project src\ProjectCal.Api\ProjectCal.Api.csproj --urls http://localhost:5009
```

You can also use `DATABASE_URL=postgresql://...`; the app converts it to an Npgsql connection string and enables SSL.
The API still stores uploaded media on the server filesystem. On Render Free that filesystem is temporary, so the next production step is Supabase Storage or S3-compatible object storage for durable photo/audio files.

Render uses port `10000` by default for Docker web services. The API Dockerfile already binds to `http://0.0.0.0:10000`.

For the full cloud sync checklist, see [`docs/CLOUD_SETUP.md`](docs/CLOUD_SETUP.md).
For Oracle Cloud Always Free deployment, see [`docs/ORACLE_ALWAYS_FREE.md`](docs/ORACLE_ALWAYS_FREE.md).
The Windows client must point to the deployed `ProjectCal.Api` backend, not directly to Supabase. For a private test build, set the hidden Windows environment variable `PROJECTCAL_API_URL` to the Render API URL before launching NotesMuchachos.

## GitHub

This repository includes `.gitignore` and a GitHub Actions workflow at `.github/workflows/ci.yml`.
After installing Git and creating an empty GitHub repository:

```powershell
git init
git add .
git commit -m "Initial NotesMuchachos app"
git branch -M main
git remote add origin https://github.com/YOUR_USERNAME/YOUR_REPOSITORY.git
git push -u origin main
```

The development auth endpoints return email confirmation and password reset tokens in the JSON response. Replace that with a real email provider before production.

## Windows EXE installer

This branch can build a traditional Windows installer:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build\Build-ExeInstaller.ps1
```

The output is:

```text
artifacts\installer\output\NotesMuchachosSetup.exe
```

GitHub Actions also includes `NotesMuchachos Installer`, which builds the setup executable as an artifact. Pushing a `v*` tag creates a GitHub Release with `NotesMuchachosSetup.exe`.

## Current MVP Behavior

- Email/password registration, email confirmation, login, refresh tokens, and password reset endpoints are implemented.
- Notes are isolated by user id and can be created, updated, deleted, searched, and synced.
- The WinUI client stores notes and attachments locally in SQLite/ApplicationData and syncs when online.
- Audio uploads create transcript rows; the API processes them with Groq when `GROQ_API_KEY` is set.
- Media storage is implemented as local server filesystem storage; Supabase Storage or S3-compatible object storage is the next production step.

## Production Checklist

- Replace dev JWT signing key.
- Add a real email sender for confirmation and password reset.
- Configure object storage instead of local filesystem media.
- Set `GROQ_API_KEY` for real Speech-to-Text.
- Add migrations instead of `EnsureCreated`.
- Harden admin diagnostics behind an admin policy.
