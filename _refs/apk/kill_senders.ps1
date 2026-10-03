[Console]::OutputEncoding = [Text.Encoding]::UTF8
$p = Get-CimInstance Win32_Process -Filter "Name='node.exe'" | Where-Object { $_.CommandLine -match 'airkiss_send|attempt\.mjs|drive\.mjs|poll_ssid|es_causal|daemon_' }
if (-not $p) { Write-Output '没有残留发包进程'; exit }
foreach ($x in $p) { Stop-Process -Id $x.ProcessId -Force; Write-Output ('killed ' + $x.ProcessId + ' :: ' + $x.CommandLine.Substring(0, [Math]::Min(70, $x.CommandLine.Length))) }
