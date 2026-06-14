param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputRoot = "artifacts/installer",
    [switch]$SkipInstaller
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$outputRootPath = Join-Path $repo $OutputRoot
$inputPath = Join-Path $outputRootPath "input"
$outputPath = Join-Path $outputRootPath "output"
$gitCommit = "local"
try {
    $gitCommit = (& git -C $repo rev-parse HEAD).Trim()
    if (-not $gitCommit) {
        $gitCommit = "local"
    }
}
catch {
    $gitCommit = "local"
}

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath failed with exit code $LASTEXITCODE"
    }
}

Remove-Item $inputPath,$outputPath -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $inputPath,$outputPath | Out-Null

$iconSource = Join-Path $repo "src/ProjectCal.Client/Assets/AppIcon.ico"
if (Test-Path $iconSource) {
    Copy-Item $iconSource (Join-Path $inputPath "NotesMuchachos.ico") -Force
}

Invoke-Checked dotnet @(
    "publish", (Join-Path $repo "src/ProjectCal.Api/ProjectCal.Api.csproj"),
    "-c", $Configuration,
    "-r", $Runtime,
    "--self-contained", "true",
    "-o", (Join-Path $inputPath "Api"),
    "/p:PublishSingleFile=false"
)

Invoke-Checked dotnet @(
    "publish", (Join-Path $repo "src/ProjectCal.Worker/ProjectCal.Worker.csproj"),
    "-c", $Configuration,
    "-r", $Runtime,
    "--self-contained", "true",
    "-o", (Join-Path $inputPath "Worker"),
    "/p:PublishSingleFile=false",
    "/p:ErrorOnDuplicatePublishOutputFiles=false"
)

Invoke-Checked dotnet @(
    "publish", (Join-Path $repo "src/ProjectCal.Client/ProjectCal.Client.csproj"),
    "-c", $Configuration,
    "-r", $Runtime,
    "--self-contained", "true",
    "-o", (Join-Path $inputPath "Client"),
    "/p:ProjectCalExeInstaller=true",
    "/p:WindowsPackageType=None",
    "/p:WindowsAppSDKSelfContained=true",
    "/p:PublishSingleFile=false",
    "/p:PublishTrimmed=false",
    "/p:GitCommit=$gitCommit"
)

Invoke-Checked dotnet @(
    "publish", (Join-Path $repo "src/ProjectCal.Launcher/ProjectCal.Launcher.csproj"),
    "-c", $Configuration,
    "-r", $Runtime,
    "--self-contained", "true",
    "-o", $inputPath,
    "/p:PublishSingleFile=true"
)

$iscc = Get-Command iscc.exe -ErrorAction SilentlyContinue
if (-not $iscc) {
    $candidates = @(
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    )
    $iscc = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}

if ($SkipInstaller -or -not $iscc) {
    Write-Host "Installer input prepared at: $inputPath"
    Write-Host "Install Inno Setup 6 and run this script again to produce NotesMuchachosSetup.exe."
    exit 0
}

$compiler = if ($iscc -is [System.Management.Automation.CommandInfo]) { $iscc.Source } else { $iscc }
Invoke-Checked $compiler @(
    (Join-Path $repo "installer/ProjectCal.iss"),
    "/DSourceDir=$inputPath",
    "/DOutputDir=$outputPath"
)

Write-Host "Installer created at: $(Join-Path $outputPath 'NotesMuchachosSetup.exe')"
