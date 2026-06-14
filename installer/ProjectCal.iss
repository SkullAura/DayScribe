#define AppName "NotesMuchachos"
#define AppVersion "1.0.0"
#ifndef SourceDir
#define SourceDir "..\artifacts\installer\input"
#endif
#ifndef OutputDir
#define OutputDir "..\artifacts\installer\output"
#endif

[Setup]
AppId={{6B4FEF6F-835D-4B0F-A1F7-2F59F20D4117}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=NotesMuchachos
DefaultDirName={autopf}\NotesMuchachos
DefaultGroupName=NotesMuchachos
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=NotesMuchachosSetup
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile={#SourceDir}\NotesMuchachos.ico
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\NotesMuchachos"; Filename: "{app}\ProjectCal.Launcher.exe"; WorkingDir: "{app}"; IconFilename: "{app}\NotesMuchachos.ico"
Name: "{autodesktop}\NotesMuchachos"; Filename: "{app}\ProjectCal.Launcher.exe"; WorkingDir: "{app}"; IconFilename: "{app}\NotesMuchachos.ico"; Tasks: desktopicon

[Run]
Filename: "{app}\ProjectCal.Launcher.exe"; Description: "Launch NotesMuchachos"; Flags: nowait postinstall skipifsilent
