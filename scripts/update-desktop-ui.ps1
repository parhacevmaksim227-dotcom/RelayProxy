$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$desktopLocation = [Environment]::GetFolderPath('Desktop')
$stableFolder = Join-Path $desktopLocation 'RelayProxy-1.0-win-x64\RelayProxy'
$stableExe = Join-Path $stableFolder 'RelayProxy.exe'
$running = @(Get-Process RelayProxy -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($desktopLocation + '\RelayProxy-', [StringComparison]::OrdinalIgnoreCase) })
$wasConnected = [bool](Get-NetTCPConnection -LocalAddress '127.0.0.1' -LocalPort 19871 -State Listen -ErrorAction SilentlyContinue)
$folders = @($stableFolder) + @($running | ForEach-Object { Split-Path $_.Path -Parent }) | Select-Object -Unique
foreach ($process in $running) {
    $helper = Start-Process -FilePath $process.Path -ArgumentList '--exit' -WindowStyle Hidden -PassThru -Wait
    if ($helper.ExitCode -ne 0 -or -not $process.WaitForExit(15000)) { throw 'Relay did not close normally; update stopped.' }
}
foreach ($folder in $folders) {
    if (-not (Test-Path -LiteralPath $folder -PathType Container)) { throw "Missing installation folder: $folder" }
    Copy-Item -Path (Join-Path $projectRoot 'outputs\RelayProxy\*') -Destination $folder -Recurse -Force
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'outputs\RelayProxy-1.9-win-x64.zip') -Destination $desktopLocation -Force
$shortcutShell = New-Object -ComObject WScript.Shell
foreach ($name in @('Relay — все приложения.lnk', 'Chrome через Relay.lnk')) {
    $path = Join-Path $desktopLocation $name
    if (Test-Path -LiteralPath $path) {
        $shortcut = $shortcutShell.CreateShortcut($path)
        $shortcut.TargetPath = $stableExe
        $shortcut.WorkingDirectory = $stableFolder
        $shortcut.IconLocation = (Join-Path $stableFolder 'relay-1.8.ico') + ',0'
        $shortcut.Save()
    }
}
Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class RelayShellRefresh { [DllImport("shell32.dll", CharSet=CharSet.Unicode)] public static extern void SHChangeNotify(uint change, uint flags, string item, IntPtr unused); }'
foreach ($folder in $folders) { [RelayShellRefresh]::SHChangeNotify(0x2000, 0x5, (Join-Path $folder 'RelayProxy.exe'), [IntPtr]::Zero) }
if ($wasConnected) { Start-Process -FilePath $stableExe -ArgumentList '--connect' -WindowStyle Normal }
else { Start-Process -FilePath $stableExe -WindowStyle Normal }
Write-Output "PASS Relay 1.9 installed; executable/window/shortcut icons updated; desktop ZIP copied; previous connection=$wasConnected."
