<#
.SYNOPSIS
    Pubblica CUS per Windows x64 e crea l'installer con Inno Setup.

.DESCRIPTION
    1. dotnet publish di Cus.App.Windows, Release, self-contained win-x64 (non serve il runtime .NET
       sul PC di destinazione), in artifacts/publish/win-x64.
    2. ISCC compila installer/cus.iss e mette il setup in artifacts/installer.

    La cartella artifacts/ è ignorata da git.

.EXAMPLE
    ./scripts/publish.ps1 -Version 0.1.0
.EXAMPLE
    ./scripts/publish.ps1 -SkipInstaller   # solo la cartella pubblicata
#>
param(
    [string]$Version = '0.1.0',
    [string]$Runtime = 'win-x64',
    [switch]$SkipInstaller,
    [string]$Iscc
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src/Cus.App.Windows/Cus.App.Windows.csproj'
$publishDir = Join-Path $root "artifacts/publish/$Runtime"
$installerDir = Join-Path $root 'artifacts/installer'

if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

dotnet publish $project -c Release -r $Runtime --self-contained true `
    -p:Version=$Version -p:DebugType=none -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish fallito (exit code $LASTEXITCODE)." }

# DebugType=none non basta: i pacchetti nativi (SkiaSharp, HarfBuzzSharp) portano i loro .pdb, ~100 MB.
Get-ChildItem $publishDir -Recurse -Filter *.pdb | Remove-Item -Force

Write-Host "Pubblicato in $publishDir"
if ($SkipInstaller) { return }

if (-not $Iscc) {
    $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    $candidates = @(
        $cmd.Source
        "$env:ProgramFiles\Inno Setup 7\ISCC.exe"
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
        "$env:LOCALAPPDATA\Programs\Inno Setup 7\ISCC.exe"
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    ) | Where-Object { $_ -and (Test-Path $_) }
    $Iscc = $candidates | Select-Object -First 1
    if (-not $Iscc) { throw 'ISCC.exe non trovato: installa Inno Setup o passa -Iscc <percorso>.' }
}

& $Iscc "/DAppVersion=$Version" "/DSourceDir=$publishDir" "/DOutputDir=$installerDir" `
    (Join-Path $root 'installer/cus.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC fallito (exit code $LASTEXITCODE)." }

Write-Host "Installer in $installerDir"
