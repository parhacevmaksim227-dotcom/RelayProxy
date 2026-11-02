$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$engineDir = Join-Path $projectRoot 'RelayProxy\engine'
$downloadDir = Join-Path $projectRoot 'work\engine-download'
New-Item -ItemType Directory -Force $engineDir,$downloadDir | Out-Null
$archive = Join-Path $downloadDir 'sing-box-1.14.4-windows-amd64.zip'
Invoke-WebRequest 'https://github.com/SagerNet/sing-box/releases/download/v1.14.4/sing-box-1.14.4-windows-amd64.zip' -OutFile $archive
$expectedHash = '99185d75d314a07dbba4a130c8ad986cbeb629a07927975636b341ca61f991df'
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expectedHash) { throw 'SHA-256 mismatch. Download rejected.' }
Expand-Archive -LiteralPath $archive -DestinationPath $downloadDir -Force
$unpacked = Join-Path $downloadDir 'sing-box-1.14.4-windows-amd64'
Copy-Item -LiteralPath (Join-Path $unpacked 'sing-box.exe') -Destination $engineDir
Copy-Item -LiteralPath (Join-Path $unpacked 'LICENSE') -Destination $engineDir
Invoke-WebRequest 'https://www.gnu.org/licenses/gpl-3.0.txt' -OutFile (Join-Path $engineDir 'GPL-3.0.txt')
Invoke-WebRequest 'https://codeload.github.com/SagerNet/sing-box/tar.gz/refs/tags/v1.14.4' -OutFile (Join-Path $engineDir 'sing-box-1.14.4-source.tar.gz')
Write-Output 'Verified sing-box 1.14.4 is ready.'
