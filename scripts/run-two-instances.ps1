<#
.SYNOPSIS
    Avvia due istanze di CUS sullo stesso PC, come se fossero due dispositivi diversi.

.DESCRIPTION
    Ogni istanza ha un profilo proprio (DB, identità e chiavi separati) e una porta propria, e
    conosce l'altra come peer manuale su 127.0.0.1. Ascoltano solo in loopback, quindi il firewall
    di Windows non chiede nulla.

    -Mode Simulated (default): entrambe con clipboard simulata, per provare sessioni, consenso,
      sola lettura e file senza toccare la clipboard di Windows.
    -Mode RealAndSimulated: "andrea" usa la clipboard reale di Windows (con indicatore vero in alto
      a destra), "luca" quella simulata. Serve a provare la clipboard reale e i file lazy in Esplora
      risorse con un solo PC. Mai due istanze con clipboard reale insieme.
#>
param(
    [ValidateSet('Simulated', 'RealAndSimulated')]
    [string]$Mode = 'Simulated',
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src/Cus.App.Windows/Cus.App.Windows.csproj'

dotnet build $project -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Build fallita (exit code $LASTEXITCODE)." }

$exe = Join-Path $root "src/Cus.App.Windows/bin/$Configuration/net10.0-windows10.0.17763.0/cus.exe"
$first = if ($Mode -eq 'RealAndSimulated') { 'real' } else { 'simulated' }

Start-Process $exe -ArgumentList '--profile', 'andrea', '--name', 'Andrea', '--port', '23700', '--bind', '127.0.0.1',
    '--clipboard', $first, '--peer', '127.0.0.1:23702'
Start-Process $exe -ArgumentList '--profile', 'luca', '--name', 'Luca', '--port', '23702', '--bind', '127.0.0.1',
    '--clipboard', 'simulated', '--peer', '127.0.0.1:23700'
