#Requires -Version 5.1
<#
.SYNOPSIS
    Builds every release artifact for EasyPDF locally.

.DESCRIPTION
    Runs publish.ps1 (self-contained, so the installed app needs nothing else), packages the
    per-user MSI with the WiX command line tool, zips the same folder as a portable build and
    writes SHA256SUMS.txt. Everything lands in <repo>\artifacts, which is what the GitHub
    release workflow uploads.

    None of this touches %LOCALAPPDATA%\EasyPDF; user settings are never part of a build.

    One-time setup: dotnet tool install --global wix --version 5.0.2

.EXAMPLE
    .\build\release.ps1 -Version 1.2.3
#>
[CmdletBinding()]
param(
    # MSI compares version numbers only, so a -prerelease suffix is deliberately not allowed:
    # it would be ignored and the upgrade would not be detected.
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')]
    [string]$Version,

    [string]$Configuration = 'Release',
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'

function Step([string]$message) {
    Write-Host ''
    Write-Host "==> $message" -ForegroundColor Cyan
}

try {
    $repoRoot = Split-Path -Parent $PSScriptRoot
    $artifacts = Join-Path $repoRoot 'artifacts'
    $publishDir = (Join-Path $artifacts 'publish').TrimEnd('\')
    $wxs = Join-Path $repoRoot 'installer\EasyPDF.wxs'
    $publishScript = Join-Path $PSScriptRoot 'publish.ps1'

    foreach ($required in @($wxs, $publishScript)) {
        if (-not (Test-Path -LiteralPath $required)) {
            throw "Missing build input: $required"
        }
    }

    $wix = Get-Command wix -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $wix) {
        throw 'The WiX command line tool was not found. Install it with: dotnet tool install --global wix --version 5.0.2'
    }
    $wixExe = if ($wix.Source) { $wix.Source } else { 'wix' }

    New-Item -ItemType Directory -Path $artifacts -Force | Out-Null

    $msiPath = Join-Path $artifacts "EasyPDF-$Version-x64.msi"
    $zipPath = Join-Path $artifacts "EasyPDF-$Version-win-x64.zip"
    $sumsPath = Join-Path $artifacts 'SHA256SUMS.txt'

    # 1. Publish -----------------------------------------------------------------------
    if ($SkipPublish) {
        Step 'Publish skipped, reusing the existing publish folder'
        if (-not (Test-Path -LiteralPath (Join-Path $publishDir 'EasyPDF.exe'))) {
            throw "-SkipPublish was used but '$publishDir' does not contain EasyPDF.exe. Run without -SkipPublish."
        }
    } else {
        Step "Publishing $Version"
        & $publishScript -Configuration $Configuration -Version $Version -SelfContained -OutDir $publishDir
        if ($LASTEXITCODE -ne 0) {
            throw "publish.ps1 failed with exit code $LASTEXITCODE."
        }
        # Checked separately: an exit code alone is not proof that the output is there.
        if (-not (Test-Path -LiteralPath (Join-Path $publishDir 'EasyPDF.exe'))) {
            throw "publish.ps1 finished but '$publishDir' does not contain EasyPDF.exe."
        }
    }

    # 2. MSI ---------------------------------------------------------------------------
    Step "Building $(Split-Path -Leaf $msiPath)"
    if (Test-Path -LiteralPath $msiPath) {
        Remove-Item -LiteralPath $msiPath -Force
    }

    $wixArgs = @(
        'build',
        '-nologo',
        '-arch', 'x64',
        '-d', "Version=$Version",
        '-d', "PublishDir=$publishDir",
        '-o', $msiPath,
        $wxs
    )
    & $wixExe @wixArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Host 'This build expects WiX 5. Install it with: dotnet tool install --global wix --version 5.0.2' -ForegroundColor Yellow
        Write-Host 'WiX 6 and later require accepting the paid Open Source Maintenance Fee licence, which is why v5 is pinned.' -ForegroundColor Yellow
        # Never leave a half-written installer behind for the next step to pick up.
        if (Test-Path -LiteralPath $msiPath) {
            Remove-Item -LiteralPath $msiPath -Force -ErrorAction SilentlyContinue
        }
        throw "wix build failed with exit code $LASTEXITCODE."
    }

    if (-not (Test-Path -LiteralPath $msiPath)) {
        throw "wix build reported success but $msiPath is missing."
    }
    # WiX leaves its debug symbols beside the package; they are not part of a release.
    Remove-Item -LiteralPath ([System.IO.Path]::ChangeExtension($msiPath, '.wixpdb')) -Force -ErrorAction SilentlyContinue

    # 3. Portable zip ------------------------------------------------------------------
    Step "Building $(Split-Path -Leaf $zipPath)"
    if (Test-Path -LiteralPath $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }

    # ZipFile rather than Compress-Archive: it is far quicker on a self-contained publish and
    # keeps the folder structure without a wildcard path.
    Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
    try {
        [System.IO.Compression.ZipFile]::CreateFromDirectory(
            $publishDir,
            $zipPath,
            [System.IO.Compression.CompressionLevel]::Optimal,
            $false)
    }
    catch {
        # A zip that stopped half way - out of disk, most likely - must not survive as an artifact.
        if (Test-Path -LiteralPath $zipPath) {
            Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue
        }
        throw "Could not build $(Split-Path -Leaf $zipPath): $($_.Exception.Message)"
    }

    # 4. Checksums ---------------------------------------------------------------------
    Step 'Writing SHA256SUMS.txt'
    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($file in @($msiPath, $zipPath)) {
        $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
        $lines.Add("$hash  $(Split-Path -Leaf $file)")
    }
    [System.IO.File]::WriteAllLines($sumsPath, $lines.ToArray(), (New-Object System.Text.UTF8Encoding($false)))

    # 5. Summary -----------------------------------------------------------------------
    Step "Release $Version"
    @($msiPath, $zipPath, $sumsPath) |
        ForEach-Object { Get-Item -LiteralPath $_ } |
        Select-Object @{ Name = 'Artifact'; Expression = { $_.Name } },
                      @{ Name = 'Size'; Expression = { '{0,8:N1} MB' -f ($_.Length / 1MB) } } |
        Format-Table -AutoSize |
        Out-String |
        Write-Host

    Write-Host "Output folder: $artifacts"
    Write-Host 'Silent install/update command: msiexec /i "<msi>" /qn /norestart'
}
catch {
    Write-Host ''
    Write-Host "RELEASE FAILED: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
