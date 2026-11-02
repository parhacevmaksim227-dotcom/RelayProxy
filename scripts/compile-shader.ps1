$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$fxc = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter fxc.exe -ErrorAction SilentlyContinue | Where-Object FullName -like '*\x64\*' | Sort-Object FullName -Descending | Select-Object -First 1
if (-not $fxc) { throw 'fxc.exe not found. Install the Windows SDK.' }
$source = Join-Path $projectRoot 'RelayProxy\Desktop\Glass.fx'
$output = Join-Path $projectRoot 'RelayProxy\Desktop\Glass.ps'
& $fxc.FullName /nologo /T ps_3_0 /E main /O3 /Fo $output $source
if ($LASTEXITCODE -ne 0) { throw 'Shader compilation failed.' }
Write-Output "Ready: $output"
