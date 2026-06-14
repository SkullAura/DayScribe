# NotesMuchachos EXE installer

This branch builds a traditional Windows `NotesMuchachosSetup.exe` installer.

## Build

Install Inno Setup 6:

```powershell
winget install --id JRSoftware.InnoSetup -e --source winget --silent --accept-package-agreements --accept-source-agreements
```

Then run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build\Build-ExeInstaller.ps1
```

The installer will be created at:

```text
artifacts\installer\output\NotesMuchachosSetup.exe
```

If Inno Setup is not installed, the script still prepares the installable app files at:

```text
artifacts\installer\input
```

You can also prepare the files without creating the setup executable:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build\Build-ExeInstaller.ps1 -SkipInstaller
```

## What the installer includes

- `ProjectCal.Launcher.exe`
- WinUI client
- Local API
- Worker
- bundled `whisper.cpp`
- bundled tiny multilingual Whisper model

The launcher starts the API and worker hidden, then opens the client.
For standalone installs it stores the local SQLite database and media under:

```text
%LOCALAPPDATA%\ProjectCal\data
```
