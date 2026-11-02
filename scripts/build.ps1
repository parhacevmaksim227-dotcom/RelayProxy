$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$localSdk = Join-Path $projectRoot 'work\tooling\dotnet\dotnet.exe'
$dotnetExe = if (Test-Path -LiteralPath $localSdk) { $localSdk } else { 'dotnet' }
if (-not (Test-Path -LiteralPath (Join-Path $projectRoot 'RelayProxy\engine\sing-box.exe'))) { throw 'Run scripts/get-engine.ps1 first.' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
& $dotnetExe publish (Join-Path $projectRoot 'RelayProxy\RelayProxy.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true -p:DebugType=None -o (Join-Path $projectRoot 'outputs\RelayProxy') --nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md'),(Join-Path $projectRoot 'LICENSE.txt') -Destination (Join-Path $projectRoot 'outputs\RelayProxy')
Write-Output 'Ready: outputs/RelayProxy/RelayProxy.exe'
