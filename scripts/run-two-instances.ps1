<#
.SYNOPSIS
    Avvia due istanze di CUS sullo stesso PC, come se fossero due dispositivi diversi.

.DESCRIPTION
    Ogni istanza ha un profilo proprio (DB, identità e chiavi separati), una porta propria
    e una clipboard simulata, così le due istanze non si contendono la clipboard di Windows.
    Ciascuna conosce l'altra come peer manuale su 127.0.0.1.
#>
param(
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src/Cus.App.Windows/Cus.App.Windows.csproj'

dotnet build $project -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Build fallita (exit code $LASTEXITCODE)." }

$exe = Join-Path $root "src/Cus.App.Windows/bin/$Configuration/net10.0-windows10.0.17763.0/cus.exe"

Start-Process $exe -ArgumentList '--profile', 'alice', '--port', '23700', '--clipboard', 'simulated', '--peer', '127.0.0.1:23702'
Start-Process $exe -ArgumentList '--profile', 'bob', '--port', '23702', '--clipboard', 'simulated', '--peer', '127.0.0.1:23700'
