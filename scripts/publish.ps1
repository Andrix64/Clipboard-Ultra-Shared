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
    # Semver: finisce in -p:Version e nel nome dell'installer, niente altri caratteri.
    [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')]
    [string]$Version = '0.1.0',
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',
    [switch]$SkipInstaller,
    [string]$Iscc
)

$ErrorActionPreference = 'Stop'
$root = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$artifacts = [System.IO.Path]::GetFullPath((Join-Path $root 'artifacts'))
$project = Join-Path $root 'src/Cus.App.Windows/Cus.App.Windows.csproj'
$publishDir = [System.IO.Path]::GetFullPath((Join-Path $artifacts "publish/$Runtime"))
$installerDir = Join-Path $artifacts 'installer'

# La cartella da svuotare deve stare dentro <root>/artifacts: mai cancellare altro per un parametro sbagliato.
if (-not $publishDir.StartsWith($artifacts + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Cartella di pubblicazione fuori da ${artifacts}: $publishDir"
}
if (Test-Path -LiteralPath $publishDir) { Remove-Item -LiteralPath $publishDir -Recurse -Force }

dotnet publish $project -c Release -r $Runtime --self-contained true `
    -p:Version=$Version -p:DebugType=none -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish fallito (exit code $LASTEXITCODE)." }

# DebugType=none non basta: i pacchetti nativi (SkiaSharp, HarfBuzzSharp) portano i loro .pdb, ~100 MB.
Get-ChildItem $publishDir -Recurse -Filter *.pdb | Remove-Item -Force

Write-Host "Pubblicato in $publishDir"
if ($SkipInstaller) { return }

if (-not $Iscc) {
    # Solo cartelle di Program Files (scrivibili solo dagli amministratori): né il PATH né
    # %LOCALAPPDATA%, dove qualunque programma dell'utente potrebbe mettere un ISCC.exe falso.
    $candidates = @(
        "$env:ProgramFiles\Inno Setup 7\ISCC.exe"
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
        "${env:ProgramFiles(x86)}\Inno Setup 7\ISCC.exe"
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) }
    $Iscc = $candidates | Select-Object -First 1
    if (-not $Iscc) { throw 'ISCC.exe non trovato in Program Files: installa Inno Setup o passa -Iscc <percorso>.' }
}
elseif (-not (Test-Path -LiteralPath $Iscc -PathType Leaf)) {
    throw "ISCC.exe non trovato: $Iscc"
}

& $Iscc "/DAppVersion=$Version" "/DSourceDir=$publishDir" "/DOutputDir=$installerDir" `
    (Join-Path $root 'installer/cus.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC fallito (exit code $LASTEXITCODE)." }

Write-Host "Installer in $installerDir"
