# Oracle Cloud Always Free deployment

This guide deploys ProjectCal API and Worker on one Oracle Cloud Always Free VM.

## What to create

Recommended free VM:

- Image: Ubuntu 24.04 or Ubuntu 22.04
- Shape: `VM.Standard.A1.Flex`
- OCPU/RAM: start with `2 OCPU / 12 GB RAM`, or use up to `4 OCPU / 24 GB RAM`
- Boot volume: keep it within Always Free limits
- Public IPv4: enabled

Oracle currently lists Always Free Arm Compute as Ampere A1 capacity that can be used as one VM or split across VMs. Always choose resources marked `Always Free-eligible` in the Oracle console.

## 1. Create the VM

1. Open Oracle Cloud Console.
2. Go to `Compute -> Instances`.
3. Click `Create instance`.
4. Name it `projectcal`.
5. Choose Ubuntu.
6. Change shape to `Ampere -> VM.Standard.A1.Flex`.
7. Pick `2 OCPU / 12 GB RAM` first.
8. Add or download an SSH key.
9. Create the instance.

## 2. Open Oracle network port

The app API uses port `8080`.

In Oracle Console:

1. Open the instance.
2. Open its `Virtual cloud network`.
3. Open the subnet security list or network security group.
4. Add ingress rule:

```text
Source CIDR: 0.0.0.0/0
IP Protocol: TCP
Destination Port Range: 8080
Description: ProjectCal API
```

SSH port `22` should already be open.

## 3. SSH into the VM

From Windows PowerShell:

```powershell
ssh -i C:\path\to\oracle-key.key ubuntu@YOUR_ORACLE_PUBLIC_IP
```

## 4. Install Docker

On the VM:

```bash
git clone https://github.com/SkullAura/DayScribe.git
cd DayScribe
git checkout exe-installer
chmod +x deploy/oracle/install-docker-ubuntu.sh
./deploy/oracle/install-docker-ubuntu.sh
```

Log out and SSH back in so Docker group permissions apply.

## 5. Configure environment variables

Create `.env` on the VM:

```bash
cd DayScribe
cp deploy/cloud.env.example .env
nano .env
```

Set at minimum:

```text
SUPABASE_DB_CONNECTION_STRING=Host=...;Port=6543;Database=postgres;Username=postgres....;Password=...;SSL Mode=Require
Jwt__SigningKey=replace-with-a-long-random-secret-at-least-32-bytes
```

Optional transcription:

```text
OPENAI_API_KEY=sk-...
OpenAI__TranscriptionModel=gpt-4o-mini-transcribe
```

If `OPENAI_API_KEY` is empty, the worker will use the development stub text.

## 6. Start ProjectCal backend

```bash
docker compose -f deploy/oracle/docker-compose.oracle.yml --env-file .env up -d --build
```

Check logs:

```bash
docker compose -f deploy/oracle/docker-compose.oracle.yml --env-file .env logs -f
```

Health check:

```bash
curl http://localhost:8080/health
```

From your Windows browser:

```text
http://YOUR_ORACLE_PUBLIC_IP:8080/health
```

## 7. Connect the Windows app

In ProjectCal:

1. Open `Settings`.
2. Set `Sync mode` to `Cloud`.
3. Set `Cloud API URL` to:

```text
http://YOUR_ORACLE_PUBLIC_IP:8080
```

4. Save settings.
5. Login/register again.
6. Press `Sync`.

Use the same `Cloud API URL` on another device.

## 8. Updating the server later

```bash
cd DayScribe
git pull
docker compose -f deploy/oracle/docker-compose.oracle.yml --env-file .env up -d --build
```

## Notes

- The included local `whisper.cpp` bundle is Windows x64. On Oracle Linux/Arm, use OpenAI transcription or leave transcription disabled/stubbed for now.
- API and Worker share the Docker volume `projectcal-media`; this is required for photo/audio sync and transcription.
- For a polished public release, add a domain and HTTPS reverse proxy later.
