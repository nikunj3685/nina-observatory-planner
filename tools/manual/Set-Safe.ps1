<#
Switches the test safety monitor started by Start-SimulatorNina.ps1.
  .\Set-Safe.ps1 -Safe      clear sky
  .\Set-Safe.ps1 -Unsafe    clouds / rain
  .\Set-Safe.ps1            show the current state
#>
param([switch] $Safe, [switch] $Unsafe)
$base = "http://127.0.0.1:32850/control"
if ($Safe) { Invoke-RestMethod "$base/safe?value=1" -TimeoutSec 5 | Out-Null }
elseif ($Unsafe) { Invoke-RestMethod "$base/safe?value=0" -TimeoutSec 5 | Out-Null }
$s = Invoke-RestMethod "$base/state" -TimeoutSec 5
Write-Host "Safety monitor: $(if ($s.safe) { 'SAFE' } else { 'UNSAFE' })  (NINA connected: $($s.connected), reads: $($s.issafe_reads))"
