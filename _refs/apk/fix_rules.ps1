$log = "$PSScriptRoot\rule_result.txt"
$exe = "$PSScriptRoot\..\..\app\GravityPanel\bin\x64\Debug\net9.0-windows10.0.19041.0\GravityPanel.exe"
$out = @()
$blk = Get-NetFirewallApplicationFilter -ErrorAction SilentlyContinue | Where-Object { $_.Program -like '*GravityTray.exe' }
foreach ($b in $blk) {
    $r = $b | Get-NetFirewallRule
    $out += ("remove: " + $r.DisplayName + " action=" + $r.Action)
    Remove-NetFirewallRule -Name $r.Name -ErrorAction SilentlyContinue
}
$name = 'Gravity++ push stream inbound 8123'
netsh advfirewall firewall delete rule name="$name" | Out-Null
$out += (netsh advfirewall firewall add rule name="$name" dir=in action=allow protocol=TCP localport=8123 program="$exe" profile=any | Out-String)
$out += ("left: " + ((Get-NetFirewallRule -DisplayName "Gravity*" | ForEach-Object { $_.DisplayName + '=' + $_.Action }) -join ', '))
$out | Out-File -FilePath $log -Encoding UTF8
