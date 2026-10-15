#Requires -Version 5.1
<#
.SYNOPSIS
    Publishes EasyPDF into a folder that is ready to run, zip or package.

.DESCRIPTION
    Produces a plain folder (not a single file): single-file publishing would throw away the
    ReadyToRun start-up win. Framework-dependent by default (about 9 MB, needs the .NET 10
    Desktop Runtime); -SelfContained carries the runtime along (about 150 MB, needs nothing).

    Settings and the log live in %LOCALAPPDATA%\EasyPDF, never in the publish folder, so
    replacing the folder never touches them. -OutDir is refused if it points into that folder,
    because the publish folder is emptied before it is written.

.EXAMPLE
    .\build\publish.ps1
    .\build\publish.ps1 -Version 1.2.3 -SelfContained
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$Version,
    [switch]$SelfContained,
    [string]$OutDir = "$PSScriptRoot\..\artifacts\publish"
)

$ErrorActionPreference = 'Stop'

function Fail([string]$message) {
    Write-Host "ERROR: $message" -ForegroundColor Red
    exit 1
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\EasyPDF\EasyPDF.csproj'

if (-not (Test-Path -LiteralPath $project)) {
    Fail "Project not found: $project"
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Fail 'The .NET SDK ("dotnet") is not on PATH. Install the .NET 10 SDK and try again.'
}

if ($Runtime -ne 'win-x64') {
    Fail "-Runtime '$Runtime' is not supported: the bundled PDFium is x64."
}

if ($Version -and $Version -notmatch '^\d+\.\d+\.\d+(\.\d+)?(-[0-9A-Za-z.-]+)?$') {
    Fail "-Version '$Version' is not a version number (expected 1.2.3)."
}

try {
    $OutDir = [System.IO.Path]::GetFullPath($OutDir).TrimEnd('\')
} catch {
    Fail "-OutDir '$OutDir' is not a usable path."
}

# Guard against a mistyped -OutDir wiping something important: this folder gets deleted.
if ($OutDir.Length -le 3 -or $OutDir -eq $repoRoot.TrimEnd('\')) {
    Fail "-OutDir '$OutDir' is not a safe publish folder; it is emptied before publishing."
}

if ($env:LOCALAPPDATA) {
    $dataDir = (Join-Path $env:LOCALAPPDATA 'EasyPDF').TrimEnd('\')
    if ($OutDir -eq $dataDir -or $OutDir.StartsWith($dataDir + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
        Fail "-OutDir '$OutDir' is inside the settings folder '$dataDir'."
    }
}

if (Test-Path -LiteralPath $OutDir) {
    try {
        Remove-Item -LiteralPath $OutDir -Recurse -Force
    } catch {
        Fail "Could not empty '$OutDir': $($_.Exception.Message). Close anything running from that folder and try again."
    }
}

New-Item -ItemType Directory -Path $OutDir -Force | Out-Null

$dotnetArgs = @(
    'publish', $project,
    '-c', $Configuration,
    '-r', $Runtime,
    '-o', $OutDir,
    '--nologo',
    '-v', 'minimal',
    '-p:PublishReadyToRun=true',
    '-p:PublishSingleFile=false',
    '-p:DebugType=none'
)

if ($SelfContained) {
    $dotnetArgs += '--self-contained'
} else {
    $dotnetArgs += '--no-self-contained'
}

if ($Version) {
    $numericVersion = ($Version -split '-')[0]
    $dotnetArgs += @(
        "-p:Version=$Version",
        "-p:AssemblyVersion=$numericVersion",
        "-p:FileVersion=$numericVersion",
        "-p:InformationalVersion=$Version"
    )
}

$banner = "Publishing $Configuration | $Runtime | " + $(if ($SelfContained) { 'self-contained' } else { 'framework-dependent' })
if ($Version) { $banner += " | $Version" }
Write-Host $banner -ForegroundColor Cyan
& dotnet @dotnetArgs

if ($LASTEXITCODE -ne 0) {
    Fail "dotnet publish failed with exit code $LASTEXITCODE. See the build output above."
}

foreach ($required in 'EasyPDF.exe', 'EasyPDF.dll', 'pdfium.dll') {
    if (-not (Test-Path -LiteralPath (Join-Path $OutDir $required))) {
        Fail "dotnet publish reported success but $required is missing from $OutDir."
    }
}

$files = @(Get-ChildItem -LiteralPath $OutDir -Recurse -File)
$totalBytes = ($files | Measure-Object -Property Length -Sum).Sum

Write-Host ''
Write-Host "Output : $OutDir"
Write-Host "Files  : $($files.Count)"
Write-Host "Size   : $([math]::Round($totalBytes / 1MB, 1)) MB"
