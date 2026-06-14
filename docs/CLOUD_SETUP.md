# ProjectCal cloud sync setup

This guide prepares the shared backend used by the Windows app. Users do not see database URLs or transcription keys; the app talks only to ProjectCal.Api.

## 1. Rotate exposed secrets first

If any API keys or database passwords were pasted into chat, GitHub issues, screenshots, or commits, replace them before production:

- Groq/OpenAI: create a new API key and revoke the old one.
- Supabase: reset the database password if it was shared.
- JWT: generate a new long random signing key.

Never commit real secrets to this repository.

## 2. Supabase database

1. Open Supabase.
2. Select the ProjectCal project.
3. Go to `Project Settings -> Database`.
4. Copy the pooled Postgres connection string.
5. Keep it private. It will be used as `SUPABASE_DB_CONNECTION_STRING`.

The API currently creates the required schema on startup when `Database__EnsureCreated=true`.

## 3. Deploy on Render Free

For the current free setup, deploy one Render `Web Service` from this repository:

- Service type: `Web Services`
- Language/runtime: `Docker`
- Branch: the branch you pushed for deployment
- Dockerfile path: `src/ProjectCal.Api/Dockerfile`
- Instance type: `Free`

Required environment variables:

```text
Database__Provider=Postgres
SUPABASE_DB_CONNECTION_STRING=Host=...;Port=6543;Database=postgres;Username=postgres....;Password=...;SSL Mode=Require
Storage__RootPath=/app/App_Data/media
ASPNETCORE_URLS=http://+:8080
Jwt__Issuer=ProjectCal
Jwt__Audience=ProjectCal.Client
Jwt__SigningKey=replace-with-a-long-random-secret-at-least-32-bytes
GROQ_API_KEY=gsk-...
Groq__TranscriptionModel=whisper-large-v3-turbo
```

The included `render.yaml` defines the public settings and marks secrets as manual Render environment variables.

Important: Render Free filesystem is temporary. Notes, users, and transcripts persist in Supabase Postgres; uploaded audio/photo files need Supabase Storage or S3-compatible storage before production.

## 4. Health check

After deploy, open:

```text
https://YOUR_API_DOMAIN/health
```

Expected response:

```json
{
  "status": "ok",
  "service": "ProjectCal.Api"
}
```

## 5. Windows app API URL

For private testing, set the hidden API URL on Windows:

```powershell
[Environment]::SetEnvironmentVariable("PROJECTCAL_API_URL", "https://YOUR_RENDER_SERVICE.onrender.com", "User")
```

Restart ProjectCal after changing it.

Do not put the Supabase URL here. The app talks to ProjectCal.Api, and ProjectCal.Api talks to Supabase.

## 6. GitHub installer build

The workflow `.github/workflows/installer.yml` builds `ProjectCalSetup.exe`.

Manual build:

1. GitHub -> `Actions`.
2. Open `ProjectCal Installer`.
3. Click `Run workflow`.
4. Download the `ProjectCalSetup` artifact.

Release build:

```powershell
git tag v0.1.0
git push origin v0.1.0
```

GitHub will create a release with `ProjectCalSetup.exe`.
