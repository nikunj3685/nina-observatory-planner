<#
Creates a separate NINA profile for the simulator tests by copying the user's profile and pointing every device at
the ASCOM OmniSim simulators (plus the test safety monitor). The user's profile is only read, never changed.
Prints the new profile id.
#>
param(
    [Parameter(Mandatory)] [string] $ImageFolder,
    [string] $Name = "Planner Test (simulators)",
    [string] $Id = "5a7e0c1d-0000-4e2e-9e2e-0b5e1a7e5701",
    [int] $DiscoveryPort = 32228
)
$ErrorActionPreference = "Stop"
$profiles = Join-Path $env:LOCALAPPDATA "NINA\Profiles"
$source = Get-ChildItem $profiles -Filter *.profile | Where-Object { $_.BaseName -ne $Id } | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $source) { throw "No NINA profile found to copy" }

[xml]$x = Get-Content $source.FullName -Raw
$root = $x.DocumentElement
function Set-Child($parent, $name, $value) {
    $node = $parent.ChildNodes | Where-Object { $_.LocalName -eq $name } | Select-Object -First 1
    if (-not $node) { throw "Profile has no $name under $($parent.LocalName)" }
    $node.InnerText = $value
}

Set-Child $root "Id" $Id
Set-Child $root "Name" $Name
Set-Child $root.CameraSettings "Id" "ASCOM.OmniSim.Camera"
Set-Child $root.TelescopeSettings "Id" "ASCOM.OmniSim.Telescope"
Set-Child $root.TelescopeSettings "TelescopeLocationSyncDirection" "NOSYNC"   # no "sync location?" question on connect
Set-Child $root.TelescopeSettings "TimeSync" "false"
Set-Child $root.FocuserSettings "Id" "ASCOM.OmniSim.Focuser"
Set-Child $root.FilterWheelSettings "Id" "ASCOM.OmniSim.FilterWheel"
Set-Child $root.DomeSettings "Id" "ASCOM.OmniSim.Dome"
Set-Child $root.SwitchSettings "Id" "ASCOM.OmniSim.Switch"
Set-Child $root.SafetyMonitorSettings "Id" "observatory-planner-test-safety-0001"
Set-Child $root.WeatherDataSettings "Id" "No_Device"
Set-Child $root.RotatorSettings "Id" "No_Device"
Set-Child $root.FlatDeviceSettings "Id" "No_Device"
Set-Child $root.GuiderSettings "GuiderName" "Direct_Guider"
Set-Child $root.AlpacaSettings "DiscoveryPort" "$DiscoveryPort"
Set-Child $root.AlpacaSettings "DiscoveryDuration" "2"
Set-Child $root.ImageFileSettings "FilePath" ($ImageFolder.TrimEnd('\') + '\')

# Filters: keep the profile's filter entries, renamed to the simulated wheel's six filters
$names = @("Red", "Green", "Blue", "Clear", "Ha", "OIII")
$filters = $root.FilterWheelSettings.FilterWheelFilters
$template = $filters.FirstChild
while ($filters.ChildNodes.Count -gt 0) { [void]$filters.RemoveChild($filters.FirstChild) }
for ($i = 0; $i -lt $names.Count; $i++) {
    $f = $template.CloneNode($true)
    ($f.ChildNodes | Where-Object { $_.LocalName -eq "_name" }).InnerText = $names[$i]
    ($f.ChildNodes | Where-Object { $_.LocalName -eq "_position" }).InnerText = "$i"
    ($f.ChildNodes | Where-Object { $_.LocalName -eq "_focusOffset" }).InnerText = "0"
    ($f.ChildNodes | Where-Object { $_.LocalName -eq "_autoFocusFilter" }).InnerText = "false"
    [void]$filters.AppendChild($f)
}

$target = Join-Path $profiles "$Id.profile"
# NINA keeps a backup copy and may load it instead; a stale one would bring back an old test setup
Remove-Item "$target.bkp" -Force -ErrorAction SilentlyContinue
$x.Save($target)
Write-Output $Id
