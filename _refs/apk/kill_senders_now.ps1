# ASCII only. Kill any airkiss sender still running, then report what is left.
$ErrorActionPreference = 'SilentlyContinue'
Get-CimInstance Win32_Process -Filter "Name='node.exe'" | ForEach-Object {
  if ($_.CommandLine -match 'airkiss_send|gravity_provision|live_stream|daemon_') {
    Write-Output ("KILL pid=" + $_.ProcessId + " :: " + $_.CommandLine)
    Stop-Process -Id $_.ProcessId -Force
  }
}
Start-Sleep -Milliseconds 500
$left = Get-CimInstance Win32_Process -Filter "Name='node.exe'" | Where-Object { $_.CommandLine -match 'airkiss|gravity' }
if ($left) { $left | ForEach-Object { Write-Output ("still running: " + $_.ProcessId) } } else { Write-Output 'no sender left' }
Write-Output '--- UDP 1503/15103 endpoints ---'
$e = Get-NetUDPEndpoint -LocalPort 1503,15103
if ($e) { $e | ForEach-Object { Write-Output ("{0}:{1} pid={2}" -f $_.LocalAddress, $_.LocalPort, $_.OwningProcess) } } else { Write-Output 'none' }
