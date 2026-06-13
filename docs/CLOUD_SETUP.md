# ProjectCal cloud sync setup

This guide prepares the shared backend used by the Windows app when `Settings -> Sync mode -> Cloud` is selected.

## 1. Rotate exposed secrets first

If any API keys or database passwords were pasted into chat, GitHub issues, screenshots, or commits, replace them before production:

- OpenAI: create a new API key and revoke the old one.
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

## 3. Deploy the API and worker

Deploy these projects as two services:

- `src/ProjectCal.Api`
- `src/ProjectCal.Worker`

Required environment variables for both services:

```text
Database__Provider=Postgres
SUPABASE_DB_CONNECTION_STRING=Host=...;Port=6543;Database=postgres;Username=postgres....;Password=...;SSL Mode=Require
Storage__RootPath=/app/App_Data/media
```

Required only for API:

```text
ASPNETCORE_URLS=http://+:8080
Jwt__Issuer=ProjectCal
Jwt__Audience=ProjectCal.Client
Jwt__SigningKey=replace-with-a-long-random-secret-at-least-32-bytes
```

Optional for worker transcription:

```text
OPENAI_API_KEY=sk-...
OpenAI__TranscriptionModel=gpt-4o-mini-transcribe
```

Important: API and Worker must see the same media folder. If your cloud platform runs them as separate services without a shared volume, audio/photo metadata can sync but media/transcription can fail. The next production step is S3-compatible storage.

For a one-VM Oracle Cloud Always Free setup, follow [`ORACLE_ALWAYS_FREE.md`](ORACLE_ALWAYS_FREE.md). It runs API and Worker together with a shared Docker volume.

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

## 5. Windows app Cloud mode

1. Install/open ProjectCal.
2. Go to `Settings`.
3. Set `Sync mode` to `Cloud`.
4. Put your backend API domain into `Cloud API URL`.

Example:

```text
https://projectcal-api.your-domain.com
```

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
