$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$desktopPath = [Environment]::GetFolderPath('Desktop')
$installedPath = Join-Path $desktopPath 'RelayProxy-1.0-win-x64\RelayProxy'
$targetExe = Join-Path $installedPath 'RelayProxy.exe'
$resultPath = Join-Path $projectRoot 'outputs\desktop-deploy-result.txt'
try {
    foreach ($process in (Get-Process RelayProxy -ErrorAction SilentlyContinue | Where-Object Path -eq $targetExe)) {
        if ([version]$process.MainModule.FileVersionInfo.FileVersion -ge [version]'1.3.0') {
            $exitHelper = Start-Process -FilePath $targetExe -ArgumentList '--exit' -WindowStyle Hidden -PassThru -Wait
            if ($exitHelper.ExitCode -ne 0) { throw 'Cannot request Relay exit through IPC.' }
        } elseif (-not $process.CloseMainWindow()) { throw 'Cannot request normal Relay shutdown.' }
        if (-not $process.WaitForExit(15000)) { throw 'Relay has not stopped; deployment cancelled.' }
    }
    Copy-Item -Path (Join-Path $projectRoot 'outputs\RelayProxy\*') -Destination $installedPath -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $projectRoot 'outputs\RelayProxy-1.9-win-x64.zip') -Destination (Join-Path $desktopPath 'RelayProxy-1.9-win-x64.zip') -Force
    $verification = Start-Process -FilePath $targetExe -ArgumentList '--verify-startup' -WindowStyle Hidden -PassThru -Wait
    if ($verification.ExitCode -ne 0) { throw 'Scheduled startup verification failed. See relay.log.' }
    # Simulate the logon action without enabling persistent autostart for the user.
    Start-Process -FilePath $targetExe -ArgumentList '--startup' -WindowStyle Normal
    $deadline = (Get-Date).AddSeconds(45)
    do {
        if (Get-NetTCPConnection -LocalAddress '127.0.0.1' -LocalPort 19871 -State Listen -ErrorAction SilentlyContinue) { break }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)
    $response = Invoke-WebRequest 'https://api.ipify.org' -Proxy 'http://127.0.0.1:19871' -TimeoutSec 15
    if ($response.StatusCode -ne 200) { throw 'Startup proxy check failed.' }
    # A second launch restores the already-running tray instance.
    $restore = Start-Process -FilePath $targetExe -WindowStyle Normal -PassThru -Wait
    if ($restore.ExitCode -ne 0) { throw 'Cannot restore running instance.' }
    Set-Content -LiteralPath $resultPath -Value 'PASS Desktop updated; startup task verified; startup connected in tray; proxy request 200; existing instance restored.'
} catch {
    Set-Content -LiteralPath $resultPath -Value ('FAIL ' + $_.Exception.Message)
    throw
}
