[Console]::OutputEncoding = [Text.Encoding]::UTF8
$log = "$PSScriptRoot\rule_result.txt"
$out = @()
$node = (Get-Command node.exe -ErrorAction SilentlyContinue).Source
$name = 'Gravity++ push stream inbound 8123'
$out += "node = $node"
netsh advfirewall firewall delete rule name="$name" > $null 2>&1
if ($node) {
    $out += (netsh advfirewall firewall add rule name="$name" dir=in action=allow protocol=TCP localport=8123 program="$node" profile=any)
} else {
    $out += (netsh advfirewall firewall add rule name="$name" dir=in action=allow protocol=TCP localport=8123 profile=any)
}
$out += (netsh advfirewall firewall show rule name="$name")
$out | Out-File -FilePath $log -Encoding UTF8
