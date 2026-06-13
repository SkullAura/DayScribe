# ProjectCal

Native Windows notes app with registration, voice/audio transcription, photo attachments, day/hour calendar, offline-first SQLite storage, and a sync backend.

## Projects

- `src/ProjectCal.Client` - WinUI 3 Windows client.
- `src/ProjectCal.Api` - ASP.NET Core API for auth, notes, media upload, sync, and diagnostics.
- `src/ProjectCal.Worker` - background transcription worker.
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
dotnet run --project src\ProjectCal.Worker\ProjectCal.Worker.csproj --environment Development
```

For PostgreSQL and containerized API/worker:

```powershell
docker compose up --build
```

## OpenAI Transcription

Real audio transcription is handled by `src/ProjectCal.Worker`.

Set an OpenAI API key before starting the worker:

```powershell
$env:OPENAI_API_KEY = "sk-your-openai-api-key"
$env:OpenAI__TranscriptionModel = "gpt-4o-mini-transcribe"
dotnet run --project src\ProjectCal.Worker\ProjectCal.Worker.csproj --environment Development
```

The worker sends uploaded audio to `https://api.openai.com/v1/audio/transcriptions`.
The default model is `gpt-4o-mini-transcribe`; use `gpt-4o-transcribe` if you want higher accuracy.
Without `OPENAI_API_KEY`, the worker writes the development stub text.

## Supabase Postgres

ProjectCal can use Supabase as its Postgres database. Get the connection string from:

Supabase dashboard -> Project Settings -> Database -> Connection string.

For PowerShell local runs:

```powershell
$env:SUPABASE_DB_CONNECTION_STRING = "Host=...;Port=6543;Database=postgres;Username=postgres....;Password=...;SSL Mode=Require"
$env:Database__Provider = "Postgres"
$env:Jwt__SigningKey = "replace-with-a-long-random-signing-key-at-least-32-bytes"

dotnet run --project src\ProjectCal.Api\ProjectCal.Api.csproj --urls http://localhost:5009
dotnet run --project src\ProjectCal.Worker\ProjectCal.Worker.csproj
```

You can also use `DATABASE_URL=postgresql://...`; the app converts it to an Npgsql connection string and enables SSL.
The API still stores uploaded media on the local filesystem, so the API and worker must see the same media path when deployed separately.

For the full cloud sync checklist, see [`docs/CLOUD_SETUP.md`](docs/CLOUD_SETUP.md).
For Oracle Cloud Always Free deployment, see [`docs/ORACLE_ALWAYS_FREE.md`](docs/ORACLE_ALWAYS_FREE.md).
In the Windows client, `Cloud API URL` must point to the deployed `ProjectCal.Api` backend, not directly to Supabase.

## GitHub

This repository includes `.gitignore` and a GitHub Actions workflow at `.github/workflows/ci.yml`.
After installing Git and creating an empty GitHub repository:

```powershell
git init
git add .
git commit -m "Initial ProjectCal app"
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
artifacts\installer\output\ProjectCalSetup.exe
```

GitHub Actions also includes `ProjectCal Installer`, which builds the setup executable as an artifact. Pushing a `v*` tag creates a GitHub Release with `ProjectCalSetup.exe`.

## Current MVP Behavior

- Email/password registration, email confirmation, login, refresh tokens, and password reset endpoints are implemented.
- Notes are isolated by user id and can be created, updated, deleted, searched, and synced.
- The WinUI client stores notes and attachments locally in SQLite/ApplicationData and syncs when online.
- Audio uploads create pending transcript rows; the worker processes them with OpenAI when `OPENAI_API_KEY` is set.
- Media storage is implemented as local server filesystem storage; `docker-compose.yml` includes MinIO as the next step for S3-compatible object storage.

## Production Checklist

- Replace dev JWT signing key.
- Add a real email sender for confirmation and password reset.
- Configure object storage instead of local filesystem media.
- Set `OPENAI_API_KEY` for real Speech-to-Text.
- Add migrations instead of `EnsureCreated`.
- Harden admin diagnostics behind an admin policy.
