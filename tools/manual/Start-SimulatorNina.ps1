<#
Starts NINA with a sandbox profile on the ASCOM OmniSim simulators, plus a test safety monitor that you switch with
Set-Safe.ps1, so the Observatory Planner can be tried by hand without touching real equipment.

- Your own profile is only read (copied once), never changed. Your running NINA can stay open.
- Planner data (target lists, options, logs) goes to Documents\N.I.N.A\Observatory Planner Sandbox.
- Close NINA to stop; the safety monitor stops with it.
#>
param(
    [string] $NinaExe = "C:\Program Files\N.I.N.A. - Nighttime Imaging 'N' Astronomy\NINA.exe",
    [switch] $Safe,
    [switch] $RebuildProfile
)
$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$e2e = Join-Path (Split-Path -Parent $here) "e2e"
$profileId = "5a7e0c1d-0000-4e2e-9e2e-0b5e1a7e5702"
$sandbox = Join-Path ([Environment]::GetFolderPath("MyDocuments")) "N.I.N.A\Observatory Planner Sandbox"
$images = Join-Path $sandbox "Images"
New-Item -ItemType Directory -Force $images | Out-Null

# Install the newest build of the plugin
$dll = Join-Path $here "..\..\src\NINA.ObservatoryPlanner\bin\Debug\net8.0-windows\NINA.ObservatoryPlanner.dll"
$pluginDir = Join-Path $env:LOCALAPPDATA "NINA\Plugins\3.0.0\Observatory Planner"
if (Test-Path $dll) {
    New-Item -ItemType Directory -Force $pluginDir | Out-Null
    Copy-Item $dll, ([IO.Path]::ChangeExtension($dll, ".pdb")) $pluginDir -Force
}

$profilePath = Join-Path $env:LOCALAPPDATA "NINA\Profiles\$profileId.profile"
if ($RebuildProfile -or -not (Test-Path $profilePath)) {
    & (Join-Path $e2e "make_test_profile.ps1") -ImageFolder $images -Name "Planner Sandbox (simulators)" -Id $profileId | Out-Null
    Write-Host "Created profile 'Planner Sandbox (simulators)'"
}

$py = (Get-Command python).Source
$monitor = Start-Process $py -ArgumentList "`"$(Join-Path $e2e 'test_safety_monitor.py')`" 32228 32850 `"$(Join-Path $sandbox 'safety-monitor.log')`"" -PassThru -WindowStyle Hidden
try {
    Start-Sleep -Seconds 1
    $value = if ($Safe) { 1 } else { 0 }
    Invoke-RestMethod "http://127.0.0.1:32850/control/safe?value=$value" -TimeoutSec 5 | Out-Null

    $env:OBSERVATORY_PLANNER_ROOT = $sandbox
    $nina = Start-Process $NinaExe -ArgumentList "--profileid $profileId" -PassThru
    Remove-Item Env:OBSERVATORY_PLANNER_ROOT

    Write-Host ""
    Write-Host "NINA is starting with the simulator profile. The safety monitor reports $(if ($Safe) { 'SAFE' } else { 'UNSAFE' })."
    Write-Host "  Switch weather:  .\Set-Safe.ps1 -Safe    /    .\Set-Safe.ps1 -Unsafe"
    Write-Host "  Planner panel:   Imaging tab > 'Observatory Planner' tab at the bottom of the centre panel"
    Write-Host "  Planner data:    $sandbox"
    Write-Host "Close NINA to finish."
    $nina.WaitForExit()
} finally {
    if (-not $monitor.HasExited) { Stop-Process -Id $monitor.Id -Force }
}
