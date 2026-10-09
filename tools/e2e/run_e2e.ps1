<#
End-to-end test of the Observatory Planner in a real NINA 3.2 with the ASCOM OmniSim simulators.

Runs a separate NINA instance with a separate test profile (the user's NINA and profile are not touched),
switches the test safety monitor safe/unsafe, checks the simulators' state over their Alpaca API,
and takes screenshots of the NINA window. Results go to tools\e2e\out\<run>\results.json.

  powershell -File tools\e2e\run_e2e.ps1 -Scenario safety
  powershell -File tools\e2e\run_e2e.ps1 -Scenario nosafety
  negative: unsafe inside 1 Begin, 2 Start of target, imaging and 4 End, plus the wait after safe
  timing:   Delay first / Delay between, Start at / End at, two targets ending before the night ends
  single:   one target that ends by time, manual autofocus, nothing left to image
  pause:    Pause now / after frame, Start sequence (same target, mount moved, other target, camera dropped), unsafe while paused, Stop
  restart:  per-profile data, workflow auto-save, NINA restart while paused, "Start the run when NINA starts"
#>
param(
    [ValidateSet("safety", "nosafety", "ui", "negative", "timing", "single", "pause", "restart")] [string] $Scenario = "safety",
    [string] $NinaExe = "$env:ProgramFiles\N.I.N.A. - Nighttime Imaging 'N' Astronomy\NINA.exe",
    # Own ports, so a sandbox started with tools\manual\Start-SimulatorNina.ps1 (32228/32850) keeps its own safety monitor
    [int] $DiscoveryPort = 32238,
    [int] $ControlPort = 32860
)
$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = Resolve-Path (Join-Path $here "..\..")
$run = Join-Path $here ("out\" + (Get-Date -Format "yyyyMMdd-HHmmss") + "-$Scenario")
$shots = Join-Path $run "screenshots"; $images = Join-Path $run "images"; $root = Join-Path $run "planner"
New-Item -ItemType Directory -Force $shots, $images, $root | Out-Null
$progress = Join-Path $run "progress.log"
$results = New-Object System.Collections.ArrayList

function Say($msg) { $line = "$(Get-Date -Format HH:mm:ss) $msg"; Write-Output $line; Add-Content $progress $line }
function Check($name, [bool]$ok, $detail = "") {
    [void]$results.Add([pscustomobject]@{ test = $name; passed = $ok; detail = "$detail"; time = (Get-Date -Format HH:mm:ss) })
    Say ("{0} {1} {2}" -f ($(if ($ok) { "PASS" } else { "FAIL" })), $name, $detail)
}
function Save-Results { $results | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $run "results.json") -Encoding utf8 }

# ---------- simulators (OmniSim Alpaca API) ----------
$alpaca = "http://localhost:32323/api/v1"; $q = "ClientID=901&ClientTransactionID=1"
function Sim($dev, $prop, $extra = "") {
    try { $r = Invoke-RestMethod "$alpaca/$dev/0/$prop`?$q$extra" -TimeoutSec 5; if ($r.ErrorNumber -ne 0) { return "err:" + $r.ErrorNumber } ; return $r.Value } catch { return "err:" + $_.Exception.Message }
}
function SimConnect($dev, [bool]$on) { Invoke-RestMethod -Method Put "$alpaca/$dev/0/connected" -Body "Connected=$on&$q" -ContentType "application/x-www-form-urlencoded" -TimeoutSec 5 | Out-Null }
function Switches { 0..2 | ForEach-Object { Sim "switch" "getswitchvalue" "&Id=$_" } }

# ---------- test safety monitor ----------
function Set-Safe([bool]$safe) { Invoke-RestMethod "http://127.0.0.1:$ControlPort/control/safe?value=$([int]$safe)" -TimeoutSec 5 | Out-Null; Say "safety monitor -> $(if ($safe) { 'SAFE' } else { 'UNSAFE' })" }

# ---------- planner log ----------
function Events {
    $f = Get-ChildItem (Join-Path $root "Logs") -Filter "planner-*.jsonl" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $f) { return @() }
    Get-Content $f.FullName -Encoding UTF8 | Where-Object { $_ } | ForEach-Object { ($_ | ConvertFrom-Json) }
}
function Wait-Event([scriptblock]$match, [int]$timeout, [string]$what) {
    $deadline = (Get-Date).AddSeconds($timeout)
    while ((Get-Date) -lt $deadline) {
        $hit = Events | Where-Object { & $match $_.evt } | Select-Object -Last 1
        if ($hit) { return $hit }
        if ($script:nina.HasExited) { throw "NINA exited while waiting for: $what" }
        Start-Sleep -Milliseconds 500
    }
    throw "Timed out after $timeout s waiting for: $what"
}
function Phase($name, $contains = $null) { { param($e) $e.kind -eq "phase" -and $e.phase -eq $name -and (-not $contains -or $e.message -like "*$contains*") }.GetNewClosure() }
function FramesOf($target) { @(Events | Where-Object { $_.evt.kind -eq "frame" -and $_.evt.target -eq $target }).Count }
function DialogShot($name, $title) {
    $path = Join-Path $shots "$name.png"
    $deadline = (Get-Date).AddSeconds(15)
    while ($true) {
        try { & (Join-Path $here "Screenshot.ps1") -ProcessId $script:nina.Id -Path $path -Title $title | Out-Null; Say "screenshot $name.png"; return $true }
        catch { if ((Get-Date) -gt $deadline) { Say "screenshot $name failed: $_"; return $false }; Start-Sleep -Milliseconds 500 }
    }
}
function Shot($name) {
    $path = Join-Path $shots "$name.png"
    try { & (Join-Path $here "Screenshot.ps1") -ProcessId $script:nina.Id -Path $path | Out-Null; Say "screenshot $name.png" } catch { Say "screenshot $name failed: $_" }
}
function EvTime($ev) { [DateTimeOffset]::Parse($ev.time).LocalDateTime }
function PhaseEvents { @(Events | Where-Object { $_.evt.kind -eq "phase" }) }
function FrameStarts($target) { @(Events | Where-Object { $_.evt.kind -eq "info" -and $_.evt.message -like "Frame starting: $target *" } | ForEach-Object { EvTime $_ }) }
function FrameEnds($target) { @(Events | Where-Object { $_.evt.kind -eq "frame" -and $_.evt.target -eq $target } | ForEach-Object { EvTime $_ }) }
function InfoAfter($text, $after) { Events | Where-Object { $_.evt.kind -eq "info" -and $_.evt.message -like $text -and (EvTime $_) -ge $after } | Select-Object -First 1 }
function ShutDownState {
    $sw = Switches; $shutter = Sim "dome" "shutterstatus"
    SimConnect "telescope" $true; $park = Sim "telescope" "atpark"; SimConnect "telescope" $false
    [pscustomobject]@{ ok = ("$park" -eq "True" -and "$shutter" -eq "1" -and ($sw[0..1] -join ",") -eq "0,0"); text = "atpark=$park shutter=$shutter switches 0,1=$($sw[0..1] -join ',')" }
}
# wait for the phase after the given time; returns the event
function NextPhase($name, $after, $timeout, $contains = $null) {
    $deadline = (Get-Date).AddSeconds($timeout)
    while ((Get-Date) -lt $deadline) {
        $hit = PhaseEvents | Where-Object { $_.evt.phase -eq $name -and (EvTime $_) -ge $after -and (-not $contains -or $_.evt.message -like "*$contains*") } | Select-Object -First 1
        if ($hit) { return $hit }
        if ($script:nina.HasExited) { throw "NINA exited while waiting for: $name" }
        Start-Sleep -Milliseconds 300
    }
    throw "Timed out after $timeout s waiting for phase $name"
}
function MountAt($raHours, $decDeg) {
    SimConnect "telescope" $true
    $ra = [double](Sim "telescope" "rightascension"); $dec = [double](Sim "telescope" "declination")
    $dRa = [Math]::Abs(((($ra - $raHours) % 24) + 36) % 24 - 12); $ok = $dRa -lt 0.1 -and [Math]::Abs($dec - $decDeg) -lt 1
    [pscustomobject]@{ ok = $ok; text = ("mount RA {0:N3}h Dec {1:N2} deg (target {2:N3}h {3:N2} deg; J2000 vs JNow)" -f $ra, $dec, $raHours, $decDeg) }
}
function Command($text) { Add-Content (Join-Path $root "e2e-commands.txt") $text; Start-Sleep -Seconds 2 }

# ---------- targets: high in the east now, far from the meridian so no flip happens during the test ----------
[xml]$prof = Get-Content (Get-ChildItem "$env:LOCALAPPDATA\NINA\Profiles" -Filter *.profile | Where-Object { $_.BaseName -ne "5a7e0c1d-0000-4e2e-9e2e-0b5e1a7e5701" } | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
$lon = [double]$prof.DocumentElement.AstrometrySettings.Longitude
$jd = (Get-Date).ToUniversalTime().Ticks / [TimeSpan]::TicksPerDay + 1721425.5
$gmst = (280.46061837 + 360.98564736629 * ($jd - 2451545.0)) % 360
$lstHours = ((($gmst + $lon) % 360) + 360) % 360 / 15
$raA = ($lstHours + 3) % 24
$raB = ($lstHours + 3.3) % 24
$now = Get-Date

function Exposure($filter, $seconds, $count) { [ordered]@{ Id = [guid]::NewGuid(); Enabled = $true; Filter = $filter; ExposureTime = $seconds; Binning = "1x1"; Count = $count; Done = 0 } }
function Target($name, $ra, $dec, $order, $exposures, $start = $null) {
    $t = [ordered]@{ Id = [guid]::NewGuid(); Enabled = $true; Name = $name; RaHours = $ra; DecDegrees = $dec; OnStart = "SlewOnly"; Rotate = $false; PositionAngle = 0
        Start = [ordered]@{ Enabled = $false; By = "Altitude"; Altitude = 0; Time = "22:00:00" }
        End = [ordered]@{ Enabled = $false; By = "Altitude"; Altitude = 0; Time = "04:00:00" }
        Order = $order; DelayFirst = 0; DelayBetween = 0; Exposures = $exposures }
    if ($start) { $t.Start = [ordered]@{ Enabled = $true; By = "Time"; Altitude = 0; Time = $start } }
    $t
}

if ($Scenario -eq "safety") {
    $bStart = $now.AddMinutes(12).ToString("HH:mm:00")
    $targets = @(
        (Target "E2E Target A" $raA 60 "RotateThroughFilters" @((Exposure "Red" 2 3), (Exposure "Green" 2 3))),
        (Target "E2E Target B" $raB 55 "FinishEachRowFirst" @((Exposure "Ha" 2 2)) $bStart)
    )
    $workflow = "Safety and dome"; $mode = "WithSafety"
} elseif ($Scenario -eq "ui") {
    # a realistic list (like the SGP screenshots) to photograph the panel; real coordinates, slew only (no plate solver here)
    function Done($e, $n) { $e.Done = $n; $e }
    $ic = Target "IC1396" 21.672 57.567 "RotateThroughFilters" @((Done (Exposure "Ha" 300 100) 8), (Exposure "OIII" 300 60), (Exposure "OIII" 300 60))
    $ic.Exposures[2].Filter = "Red"; $ic.End = [ordered]@{ Enabled = $true; By = "Altitude"; Altitude = 39; Time = "04:00:00" }
    $sg = Target "Seagull Nebula (IC 2177)" 7.074 -10.45 "RotateThroughFilters" @((Done (Exposure "Ha" 300 80) 34), (Done (Exposure "OIII" 300 40) 40))
    $sg.Start = [ordered]@{ Enabled = $true; By = "Altitude"; Altitude = 25; Time = "23:40:00" }
    $ngc = Target "NGC 7000" 20.988 44.529 "FinishEachRowFirst" @((Done (Exposure "Ha" 180 60) 60), (Done (Exposure "OIII" 180 60) 60)); $ngc.Enabled = $false
    $m42 = Target "M42" 5.588 -5.391 "FinishEachRowFirst" @((Exposure "Red" 30 30), (Exposure "Green" 30 30), (Exposure "Blue" 30 30))
    $m42.Start = [ordered]@{ Enabled = $true; By = "Time"; Altitude = 0; Time = "00:20:00" }
    # unchecked targets for Slew now / Center now: overhead now, below the horizon now, and 4-5 deg high now
    $hi = Target "E2E High" $lstHours 50 "FinishEachRowFirst" @((Exposure "Red" 2 1)); $hi.Enabled = $false
    $below = Target "E2E Below horizon" (($lstHours + 12) % 24) -30 "FinishEachRowFirst" @((Exposure "Red" 2 1)); $below.Enabled = $false
    $low = Target "E2E Very low" $lstHours -40 "FinishEachRowFirst" @((Exposure "Red" 2 1)); $low.Enabled = $false
    $targets = @($ic, $sg, $ngc, $m42, $hi, $below, $low)
    $workflow = "Safety and dome"; $mode = "WithSafety"
} elseif ($Scenario -eq "negative") {
    $targets = @((Target "E2E Negative" $raA 60 "FinishEachRowFirst" @((Exposure "Red" 2 6))))
    $workflow = "Safety and dome"; $mode = "WithSafety"
} elseif ($Scenario -eq "timing") {
    $t1 = Target "E2E Delays" $raA 60 "FinishEachRowFirst" @((Exposure "Red" 2 3))
    $t1.DelayFirst = 10; $t1.DelayBetween = 8
    $t1End = $now.AddMinutes(4)
    $t1.End = [ordered]@{ Enabled = $true; By = "Time"; Altitude = 0; Time = $t1End.ToString("HH:mm:ss") }
    $t2Start = $now.AddMinutes(6); $t2End = $now.AddMinutes(8)
    $t2 = Target "E2E Window" $raB 55 "FinishEachRowFirst" @((Exposure "Green" 2 200)) $t2Start.ToString("HH:mm:ss")
    $t2.End = [ordered]@{ Enabled = $true; By = "Time"; Altitude = 0; Time = $t2End.ToString("HH:mm:ss") }
    $targets = @($t1, $t2)
    $workflow = "Safety and dome"; $mode = "WithSafety"
} elseif ($Scenario -eq "pause") {
    $targets = @((Target "E2E Pause" $raA 60 "FinishEachRowFirst" @((Exposure "Red" 2 60))), (Target "E2E Second" $raB 55 "FinishEachRowFirst" @((Exposure "Green" 2 30))))
    $workflow = "Safety and dome"; $mode = "WithSafety"
} elseif ($Scenario -eq "restart") {
    $targets = @((Target "E2E Restart" $raA 60 "FinishEachRowFirst" @((Exposure "Red" 2 60))))
    $workflow = "Safety and dome"; $mode = "WithSafety"
} elseif ($Scenario -eq "single") {
    $s1 = Target "E2E Single" $raA 60 "FinishEachRowFirst" @((Exposure "Red" 2 500))
    $s1End = $now.AddMinutes(4)
    $s1.End = [ordered]@{ Enabled = $true; By = "Time"; Altitude = 0; Time = $s1End.ToString("HH:mm:ss") }
    $targets = @($s1)
    $workflow = "Safety and dome"; $mode = "WithSafety"
} else {
    $targets = @((Target "E2E Target C" $raA 60 "FinishEachRowFirst" @((Exposure "Blue" 2 2))))
    $workflow = "No safety, no dome"; $mode = "WithoutSafety"
}
$list = [ordered]@{ Name = "E2E $Scenario"; Targets = $targets }
$listPath = Join-Path $root "Target lists\E2E $Scenario.json"
New-Item -ItemType Directory -Force (Split-Path $listPath) | Out-Null
$list | ConvertTo-Json -Depth 6 | Set-Content $listPath -Encoding utf8
$scenarioPath = Join-Path $run "scenario.json"
[ordered]@{
    Workflow = $workflow; TargetList = $listPath; Fast = $true; AutoStart = ($Scenario -ne "ui")
    RemoveTriggers = @("AutofocusAfterFilterChange", "PlannerAutofocusOnFilterChange")   # simulated images have no stars to focus on
    Options = [ordered]@{ RunMode = $mode; GapMinutes = 1; GapMount = "StopTrackingAndPark"; GapCloseDome = $true; AutofocusAfterBegin = $false; CloseGuiderAppOnDisconnect = $false; CloseMountAppOnDisconnect = $false; MountRecovery = $false; NightLimitEnabled = $false   # simulators: the Sun is not considered, so the test runs at any hour
                          SafeDelaySeconds = $(if ($Scenario -eq "negative") { 20 } else { 0 })
                          KeepConnected = @("Safety Monitor", "Switch", "Dome") }
} | ConvertTo-Json -Depth 5 | Set-Content $scenarioPath -Encoding utf8
Say "run folder $run"
Say ("targets: A RA {0:N2}h  B RA {1:N2}h (LST {2:N2}h); B starts {3}" -f $raA, $raB, $lstHours, $bStart)

# ---------- reset the simulated mount: a test stopped in the middle of a slew leaves OmniSim "slewing" ----------
try {
    $slewing = (Invoke-RestMethod "http://localhost:32323/api/v1/telescope/0/slewing?ClientID=7&ClientTransactionID=1" -TimeoutSec 5).Value
    if ($slewing) {
        Invoke-RestMethod -Method Put "http://localhost:32323/api/v1/telescope/0/abortslew" -Body "ClientID=7&ClientTransactionID=2" -ContentType "application/x-www-form-urlencoded" -TimeoutSec 5 | Out-Null
        Say "simulated mount was still slewing from an earlier run: slew aborted"
    }
} catch { Say "could not check the simulated mount: $_" }

# ---------- deploy plugin, profile, safety monitor ----------
$dll = Join-Path $repo "src\NINA.ObservatoryPlanner\bin\Debug\net8.0-windows\NINA.ObservatoryPlanner.dll"
$pluginDir = Join-Path $env:LOCALAPPDATA "NINA\Plugins\3.0.0\Observatory Planner"
New-Item -ItemType Directory -Force $pluginDir | Out-Null
foreach ($f in @($dll, ($dll -replace "\.dll$", ".pdb"))) {
    $target = Join-Path $pluginDir (Split-Path $f -Leaf)
    try { Copy-Item $f $target -Force -ErrorAction Stop }
    catch {
        # your own NINA is open and holds the file: move it aside (the running NINA keeps working), then copy
        Rename-Item $target ("{0}.in-use-{1}.old" -f (Split-Path $target -Leaf), (Get-Date -Format HHmmss))
        Copy-Item $f $target -Force
        Say "$(Split-Path $f -Leaf) was in use by an open NINA: moved aside as .old"
    }
}
Say "plugin copied to $pluginDir"
$profileId = & (Join-Path $here "make_test_profile.ps1") -ImageFolder $images -DiscoveryPort $DiscoveryPort
Say "test profile $profileId"
$py = (Get-Command python).Source
$safetyProc = Start-Process $py -ArgumentList "`"$(Join-Path $here 'test_safety_monitor.py')`" $DiscoveryPort $ControlPort `"$(Join-Path $run 'safety-monitor.log')`"" -PassThru -WindowStyle Hidden
Start-Sleep -Seconds 2
$startsUnsafe = $Scenario -in @("safety", "negative")
Set-Safe (-not $startsUnsafe)

# ---------- launch a separate NINA ----------
$env:OBSERVATORY_PLANNER_E2E = $scenarioPath
$env:OBSERVATORY_PLANNER_ROOT = $root
$script:nina = Start-Process $NinaExe -ArgumentList "--profileid $profileId --disable-hardware-acceleration" -PassThru
Remove-Item Env:OBSERVATORY_PLANNER_E2E, Env:OBSERVATORY_PLANNER_ROOT
Say "NINA test instance started (pid $($nina.Id))"

try {
    Wait-Event { param($e) $e.kind -eq "info" -and $e.message -like "E2E scenario loaded*" } 240 "plugin loaded the scenario" | Out-Null
    Check "Plugin loads in NINA 3.2 and builds the workflow" $true
    Start-Sleep -Seconds 4
    Command "show panel"

    if ($Scenario -eq "safety") {
        Wait-Event (Phase "WaitingForSafe") 180 "waiting for safe" | Out-Null
        Check "Starts in 'Waiting for safe' while the monitor reports unsafe" $true
        $sm = Invoke-RestMethod "http://127.0.0.1:$ControlPort/control/state" -TimeoutSec 5
        Check "Safety monitor connected by 'Keep connected'" ([bool]$sm.connected) "issafe reads: $($sm.issafe_reads)"
        Start-Sleep -Seconds 2; Shot "01-waiting-for-safe"
        Command "tab sequence"; Start-Sleep -Seconds 2; Shot "02-sequencer-workflow-four-stages"; Command "tab imaging"

        Set-Safe $true
        Wait-Event (Phase "Begin") 60 "1 Begin" | Out-Null
        Wait-Event (Phase "Imaging" "E2E Target A") 300 "imaging Target A" | Out-Null
        Check "Safe -> 1 Begin -> 2 Start of target (Target A)" $true
        $sw = Switches; Check "Stage 1 switched power on (relay switches 0,1 = 1)" (($sw[0..1] -join ",") -eq "1,1") ("switches 0,1,2 = " + ($sw -join ",") + " (switch 2 is a 0-100 dimmer in steps of 10: value 1 rounds to 0)")
        $shutter = Sim "dome" "shutterstatus"; Check "Stage 1 opened the dome (shutter 0 = open)" ("$shutter" -eq "0") "shutter=$shutter"
        $deadline = (Get-Date).AddSeconds(240); while ((FramesOf "E2E Target A") -lt 2 -and (Get-Date) -lt $deadline) { Start-Sleep 1 }
        $f1 = FramesOf "E2E Target A"; Check "Target A frames are taken and counted" ($f1 -ge 2) "$f1 frames"
        $tracking = Sim "telescope" "tracking"; Check "Mount slewed and is tracking while imaging" ("$tracking" -eq "True") "tracking=$tracking"
        Shot "03-imaging-target-A"

        Set-Safe $false
        Wait-Event (Phase "End" "unsafe") 120 "4 End (unsafe)" | Out-Null
        Check "Unsafe interrupts imaging and runs 4 End (unsafe)" $true
        $deadline = (Get-Date).AddSeconds(180)
        while ((Get-Date) -lt $deadline) { $last = (Events | Where-Object { $_.evt.kind -eq "phase" } | Select-Object -Last 1).evt; if ($last.phase -eq "WaitingForSafe") { break }; Start-Sleep 1 }
        Check "After 4 End it waits for safe again" ($last.phase -eq "WaitingForSafe") $last.message
        $fAtUnsafe = FramesOf "E2E Target A"
        $sw = Switches; Check "Stage 4 switched power off (relay switches 0,1 = 0)" (($sw[0..1] -join ",") -eq "0,0") ("switches 0,1,2 = " + ($sw -join ","))
        $shutter = Sim "dome" "shutterstatus"; Check "Stage 4 closed the dome (shutter 1 = closed)" ("$shutter" -eq "1") "shutter=$shutter"
        SimConnect "telescope" $true; $park = Sim "telescope" "atpark"; SimConnect "telescope" $false
        Check "Stage 4 parked the mount" ("$park" -eq "True") "atpark=$park"
        Shot "04-unsafe-shutdown-waiting"

        Set-Safe $true
        $beginCount = @(Events | Where-Object { $_.evt.kind -eq "phase" -and $_.evt.phase -eq "Begin" }).Count
        $deadline = (Get-Date).AddSeconds(300)
        while (@(Events | Where-Object { $_.evt.kind -eq "phase" -and $_.evt.phase -eq "Imaging" -and $_.evt.target -eq "E2E Target A" }).Count -lt 2 -and (Get-Date) -lt $deadline) { Start-Sleep 1 }
        $resumed = @(Events | Where-Object { $_.evt.kind -eq "phase" -and $_.evt.phase -eq "Imaging" -and $_.evt.target -eq "E2E Target A" }).Count -ge 2
        Check "Safe again -> 1 Begin -> resumes the same target (Target A)" $resumed
        $deadline = (Get-Date).AddSeconds(240); while ((FramesOf "E2E Target A") -le $fAtUnsafe -and (Get-Date) -lt $deadline) { Start-Sleep 1 }
        $doneAfter = (Events | Where-Object { $_.evt.kind -eq "frame" -and $_.evt.target -eq "E2E Target A" } | Select-Object -Last 1).evt.targetDone
        Check "Progress continues after the interruption (not restarted)" ($doneAfter -gt $fAtUnsafe) "frames before unsafe $fAtUnsafe, now $doneAfter"
        Shot "05-resumed-target-A"

        $deadline = (Get-Date).AddSeconds(420); while ((FramesOf "E2E Target A") -lt 6 -and (Get-Date) -lt $deadline) { Start-Sleep 1 }
        $filters = (Events | Where-Object { $_.evt.kind -eq "frame" -and $_.evt.target -eq "E2E Target A" } | ForEach-Object { $_.evt.filter }) -join ","
        Check "Target A complete: 6 frames, rotating Red/Green" ((FramesOf "E2E Target A") -eq 6) $filters
        Wait-Event (Phase "WaitingBetweenTargets" "StopTrackingAndPark") 120 "long wait before Target B" | Out-Null
        Start-Sleep -Seconds 25
        $shutter = Sim "dome" "shutterstatus"; $park = Sim "telescope" "atpark"
        Check "Long wait before Target B: mount parked and dome closed" ("$park" -eq "True" -and "$shutter" -eq "1") "atpark=$park shutter=$shutter"
        Shot "06-waiting-between-targets-parked"

        Wait-Event (Phase "Imaging" "E2E Target B") 900 "Target B at its start time" | Out-Null
        $shutter = Sim "dome" "shutterstatus"; $park = Sim "telescope" "atpark"
        Check "At Target B's start: dome reopened and mount unparked" ("$park" -eq "False" -and "$shutter" -eq "0") "atpark=$park shutter=$shutter"
        $deadline = (Get-Date).AddSeconds(180); while ((FramesOf "E2E Target B") -lt 2 -and (Get-Date) -lt $deadline) { Start-Sleep 1 }
        Shot "07-imaging-target-B"
        Wait-Event (Phase "End" "finished") 180 "4 End (finished)" | Out-Null
        Wait-Event (Phase "WaitingForNextNight") 180 "waiting for next night" | Out-Null
        Check "All targets done -> 4 End (finished) -> waits for the next night while still safe" $true
        Start-Sleep -Seconds 3
        Shot "08-finished-waiting-for-next-night"
        $saved = Get-Content $listPath -Raw | ConvertFrom-Json
        $savedDone = ($saved.Targets | ForEach-Object { $_.Exposures } | Measure-Object -Property Done -Sum).Sum
        Check "Progress saved to the target list file" ($savedDone -eq 8) "$savedDone of 8 frames in the file"
        $fits = @(Get-ChildItem $images -Recurse -File).Count
        Check "Images saved by NINA" ($fits -ge 8) "$fits files"

        Set-Safe $false
        $deadline = (Get-Date).AddSeconds(60)
        while ((Get-Date) -lt $deadline) { $last = (Events | Where-Object { $_.evt.kind -eq "phase" } | Select-Object -Last 1).evt; if ($last.phase -eq "WaitingForSafe") { break }; Start-Sleep 1 }
        Check "Dawn (unsafe) -> loops back to waiting for the next safe night" ($last.phase -eq "WaitingForSafe") $last.message
    } elseif ($Scenario -eq "ui") {
        Start-Sleep -Seconds 6
        Command "tab imaging"; Command "show panel"; Command "planner tab 0"; Start-Sleep -Seconds 2
        Shot "11-panel-targets"
        Command "planner tab 1"; Start-Sleep -Seconds 4; Shot "12-panel-equipment-and-safety"
        Command "planner tab 2"; Start-Sleep -Seconds 2; Shot "13-panel-options"
        Command "planner tab 0"
        $opened = @()
        foreach ($d in @(@("target settings", "16-target-settings", "Target Settings"), @("planning tools", "17-planning-tools", "Planning tools"),
                         @("load default", "18-load-default-workflow", "Load a default workflow"), @("delete target", "19-delete-confirmation", "Delete target"),
                         @("open list", "20-open-target-list", "Open target list"))) {
            Command "dialog $($d[0])"; Start-Sleep -Seconds 2
            $opened += (DialogShot $d[1] $d[2])
            Command "close dialogs"; Start-Sleep -Seconds 1
        }
        Check "Target Settings, Planning tools, workflow picker, delete confirmation and Open dialogs open in NINA" (-not ($opened -contains $false)) "$(@($opened | Where-Object { $_ }).Count) of $($opened.Count)"
        Check "Closing the delete confirmation keeps the target" ((Get-Content $listPath -Raw | ConvertFrom-Json).Targets.Count -eq 7)
        Command "start"
        Wait-Event (Phase "Imaging") 300 "imaging the first target" | Out-Null
        # Which target is open depends on the real sky at the time of the test (IC1396 in the evening, M42 in the morning)
        $first = (Events | Where-Object { $_.evt.phase -eq "Imaging" } | Select-Object -First 1).evt.target
        Check "Run forever starts a checked, unfinished target (never the unchecked NGC 7000)" ($first -in @("IC1396", "Seagull Nebula (IC 2177)", "M42")) $first
        Start-Sleep -Seconds 8; Command "show panel"; Start-Sleep -Seconds 2
        Shot "14-panel-running-imaging"
        Command "planner tab 1"; Start-Sleep -Seconds 4; Shot "15-panel-stages-while-running"

        # ---- Slew now / Center now from Target Settings (devices stay connected after Stop) ----
        $t0 = Get-Date
        Command "stop"
        NextPhase "Stopped" $t0 120 | Out-Null
        Command "planner tab 0"
        $t0 = Get-Date; Command "slew target 5"
        $r = $null; $deadline = (Get-Date).AddSeconds(120); while (-not $r -and (Get-Date) -lt $deadline) { Start-Sleep 1; $r = InfoAfter "Slew now E2E High:*" $t0 }
        Start-Sleep -Seconds 2; $pos = MountAt $lstHours 50
        $warned = [bool](InfoAfter "Collision warning*" $t0)
        Check "Slew now to a high target: no collision warning, the mount slews there" ($r.evt.message -like "*Slew finished*" -and $pos.ok -and -not $warned) "$($r.evt.message); $($pos.text)"

        $t0 = Get-Date; Command "slew target 6"
        $shot = DialogShot "21-collision-warning-below-horizon" "possible collision"
        $w = InfoAfter "Collision warning*" $t0
        Command "answer cancel"
        Start-Sleep -Seconds 2
        $cancel = InfoAfter "Slew cancelled after the collision warning*" $t0
        $pos = MountAt $lstHours 50
        Check "Slew now below the horizon: collision warning; Cancel leaves the mount where it is" ($shot -and $w -and $cancel -and $pos.ok) "$($w.evt.message); $($pos.text)"

        $t0 = Get-Date; Command "slew target 7"
        $shot = DialogShot "22-collision-warning-very-low" "possible collision"
        Command "answer yes"
        $r = $null; $deadline = (Get-Date).AddSeconds(120); while (-not $r -and (Get-Date) -lt $deadline) { Start-Sleep 1; $r = InfoAfter "Slew now E2E Very low:*" $t0 }
        Start-Sleep -Seconds 2; $pos = MountAt $lstHours -40
        Check "Slew now to a very low target: warning, 'Slew anyway' slews the mount" ($shot -and [bool](InfoAfter "Slew confirmed*" $t0) -and $r.evt.message -like "*Slew finished*" -and $pos.ok) "$($r.evt.message); $($pos.text)"

        $t0 = Get-Date; Command "center target 5"
        $r = $null; $deadline = (Get-Date).AddSeconds(240); while (-not $r -and (Get-Date) -lt $deadline) { Start-Sleep 1; $r = InfoAfter "Center now E2E High:*" $t0 }
        $pos = MountAt $lstHours 50
        Check "Center now (no plate solver on this PC): reports that centering did not finish instead of hanging" ($r -and $r.evt.message -notlike "*Centered.*") "$($r.evt.message); $($pos.text)"
        Shot "23-after-slew-and-center"

        # ---- edits made in the Advanced Sequencer show up in the panel ----
        Command "planner tab 1"
        $t0 = Get-Date; Command "edit stage 1"; Start-Sleep -Seconds 5; Command "report stages"; Start-Sleep -Seconds 1
        $s1 = InfoAfter "Panel stage 1:*" $t0
        Check "An instruction added in the Advanced Sequencer appears in the panel by itself (stage 1)" ($s1.evt.message -like "*Wait for Time Span  · 7 s*") $s1.evt.message
        Shot "24-panel-after-sequencer-edit"
        $t0 = Get-Date; Command "replace sequence"; Start-Sleep -Seconds 5; Command "report stages"; Start-Sleep -Seconds 1
        $s1 = InfoAfter "Panel stage 1:*" $t0; $s4 = InfoAfter "Panel stage 4:*" $t0
        Check "A different sequence opened in the Advanced Sequencer replaces the panel's stages" ($s4.evt.message -like "*Wait for Time Span  · 9 s*" -and $s1.evt.message -notlike "*Open Dome Shutter*") "$($s4.evt.message)"
        Shot "25-panel-after-sequence-replaced"

        # ---- edits in the panel are saved automatically ----
        Command "rename target 4"; Start-Sleep -Seconds 4
        $savedNames = (Get-Content $listPath -Raw | ConvertFrom-Json).Targets | ForEach-Object { $_.Name }
        Check "A target edited in the panel is saved to its list file automatically" ($savedNames -contains "M42 (renamed)") ($savedNames -join ", ")
    } elseif ($Scenario -eq "negative") {
        $t = (Get-Date).AddMinutes(-5)
        NextPhase "WaitingForSafe" $t 180 | Out-Null
        Check "Starts in 'Waiting for safe'" $true

        # wait after safe (20 s): an unsafe blip inside the wait restarts it
        $t = Get-Date; Set-Safe $true
        NextPhase "WaitingForSafe" $t 30 "waiting 20 s" | Out-Null
        Start-Sleep -Seconds 5; Set-Safe $false
        $blip = $null; $deadline = (Get-Date).AddSeconds(20); while (-not $blip -and (Get-Date) -lt $deadline) { Start-Sleep 1; $blip = InfoAfter "Unsafe again during the wait after safe*" $t }
        Start-Sleep -Seconds 3
        $earlyBegin = @(PhaseEvents | Where-Object { $_.evt.phase -eq "Begin" }).Count
        Check "Wait after safe: unsafe inside the 20 s wait does not start 1 Begin" ($blip -and $earlyBegin -eq 0) "begins so far: $earlyBegin"
        $safeAt = Get-Date; Set-Safe $true
        $b = NextPhase "Begin" $safeAt 90
        $waited = ((EvTime $b) - $safeAt).TotalSeconds
        Check "Wait after safe: 1 Begin starts only after 20 s of safe" ($waited -ge 19) ("{0:N1} s after safe" -f $waited)

        # 1) unsafe inside 1 Begin
        Set-Safe $false
        $e = NextPhase "End" (EvTime $b) 120 "unsafe"
        $between = @(PhaseEvents | Where-Object { $_.evt.phase -eq "Imaging" -and (EvTime $_) -ge (EvTime $b) -and (EvTime $_) -le (EvTime $e) }).Count
        $w = NextPhase "WaitingForSafe" (EvTime $e) 180
        $st = ShutDownState
        Check "Unsafe during 1 Begin: Begin stops, no target starts, 4 End runs and shuts down" ($between -eq 0 -and $st.ok) $st.text
        Shot "31-unsafe-during-begin"

        # 2) unsafe inside 2 Start of target (while slewing)
        $t = Get-Date; Set-Safe $true
        $i = NextPhase "Imaging" $t 180
        Set-Safe $false
        $e = NextPhase "End" (EvTime $i) 120 "unsafe"
        $w = NextPhase "WaitingForSafe" (EvTime $e) 180
        $st = ShutDownState
        Check "Unsafe during 2 Start of target: 4 End runs and shuts down" $st.ok $st.text
        Shot "32-unsafe-during-start-of-target"

        # 3) unsafe while imaging
        $t = Get-Date; Set-Safe $true
        NextPhase "Imaging" $t 180 | Out-Null
        $before = FramesOf "E2E Negative"
        $deadline = (Get-Date).AddSeconds(120); while ((FramesOf "E2E Negative") -le $before -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 300 }
        $t = Get-Date; Set-Safe $false
        $e = NextPhase "End" $t 120 "unsafe"
        $w = NextPhase "WaitingForSafe" (EvTime $e) 180
        $st = ShutDownState
        $framesAtUnsafe = FramesOf "E2E Negative"
        Check "Unsafe while imaging: the frame is abandoned, 4 End runs and shuts down" $st.ok "$($st.text); frames so far $framesAtUnsafe"
        Shot "33-unsafe-while-imaging"

        # 4) safe again while 4 End is running: End finishes first, then the wait after safe, then 1 Begin
        $t = Get-Date; Set-Safe $true
        NextPhase "Imaging" $t 180 | Out-Null
        $before = FramesOf "E2E Negative"
        $deadline = (Get-Date).AddSeconds(120); while ((FramesOf "E2E Negative") -le $before -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 300 }
        $t = Get-Date; Set-Safe $false
        $e = NextPhase "End" $t 120 "unsafe"
        Set-Safe $true
        $b = NextPhase "Begin" (EvTime $e) 180
        $order = (PhaseEvents | Where-Object { (EvTime $_) -ge (EvTime $e) -and (EvTime $_) -le (EvTime $b) } | ForEach-Object { $_.evt.phase }) -join " > "
        Check "Safe again during 4 End: End completes, then the wait after safe, then 1 Begin" ($order -like "End > WaitingForSafe > Begin") $order

        # the target finishes after all the interruptions, without lost or repeated frames
        Wait-Event (Phase "WaitingForNextNight") 400 "finished target" | Out-Null
        $frames = FramesOf "E2E Negative"
        $saved = (Get-Content $listPath -Raw | ConvertFrom-Json).Targets[0].Exposures[0].Done
        $st = ShutDownState
        Check "After 4 interruptions the target completes with exactly 6 frames, and 4 End (finished) shuts down" ($frames -eq 6 -and $saved -eq 6 -and $st.ok) "frames $frames, saved $saved, $($st.text)"
        Shot "34-finished-after-interruptions"

        # 5) unsafe at dawn, then safe: nothing left, so the equipment stays off
        $t = Get-Date; Set-Safe $false
        NextPhase "WaitingForSafe" $t 60 | Out-Null
        $t = Get-Date; Set-Safe $true
        $n = NextPhase "WaitingForNextNight" $t 90 "Nothing to image"
        Start-Sleep -Seconds 5
        $restarted = @(PhaseEvents | Where-Object { $_.evt.phase -eq "Begin" -and (EvTime $_) -ge $t }).Count
        Check "Safe again with nothing left to image: 1 Begin does not run, equipment stays off" ($restarted -eq 0) $n.evt.message
        $errors = @(PhaseEvents | Where-Object { $_.evt.phase -eq "Stopped" }).Count
        Check "The loop never stopped with an error" ($errors -eq 0)
    } elseif ($Scenario -eq "timing") {
        Set-Safe $true
        $p1 = Wait-Event (Phase "Imaging" "E2E Delays") 300 "E2E Delays"
        $deadline = (Get-Date).AddSeconds(240); while ((FramesOf "E2E Delays") -lt 3 -and (Get-Date) -lt $deadline) { Start-Sleep 1 }
        $starts = FrameStarts "E2E Delays"
        $first = ($starts[0] - (EvTime $p1)).TotalSeconds
        $gaps = @(); for ($k = 1; $k -lt $starts.Count; $k++) { $gaps += ($starts[$k] - $starts[$k - 1]).TotalSeconds }
        Check "Delay first 10 s: the first frame starts at least 10 s after 2 Start of target" ($first -ge 10) ("{0:N1} s (includes the slew)" -f $first)
        Check "Delay between 8 s: frames (2 s each) start at least 10 s apart" ($gaps.Count -eq 2 -and @($gaps | Where-Object { $_ -lt 10 }).Count -eq 0) (($gaps | ForEach-Object { "{0:N1} s" -f $_ }) -join ", ")
        Shot "41-delays-target"

        $p2 = Wait-Event (Phase "Imaging" "E2E Window") 600 "E2E Window at its start time"
        while ((Get-Date) -lt $t2End.AddSeconds(30)) { Start-Sleep 2 }
        $starts = FrameStarts "E2E Window"; $ends = FrameEnds "E2E Window"
        $late = @($ends | Where-Object { $_ -gt $t2End }).Count
        $restarts = @(PhaseEvents | Where-Object { $_.evt.phase -eq "Imaging" -and $_.evt.target -eq "E2E Window" }).Count
        Check "Start at (time): the second target's first frame starts at or after its start time" ($starts.Count -gt 0 -and $starts[0] -ge $t2Start.AddSeconds(-1)) ("start {0:HH:mm:ss}, first frame {1:HH:mm:ss}" -f $t2Start, $starts[0])
        Check "End at (time): every frame finishes before the end time, then the target stops" ($ends.Count -ge 3 -and $late -eq 0) ("end {0:HH:mm:ss}, last frame finished {1:HH:mm:ss}, {2} frames" -f $t2End, $ends[-1], $ends.Count)
        Check "At its end time the target is not slewed to again for a frame that cannot fit" ($restarts -eq 1) "2 Start of target ran $restarts time(s)"
        Shot "42-window-target"

        $e = Wait-Event (Phase "End" "finished") 180 "4 End (finished)"
        $w = Wait-Event (Phase "WaitingForNextNight") 180 "waiting after the last target"
        $st = ShutDownState
        Check "Two targets, both ended before the night ends: 4 End (finished) shuts everything down and waits" $st.ok $st.text
        Shot "43-two-targets-ended"
    } elseif ($Scenario -eq "single") {
        Set-Safe $true
        Wait-Event (Phase "Imaging" "E2E Single") 300 "E2E Single" | Out-Null
        $deadline = (Get-Date).AddSeconds(120); while ((FramesOf "E2E Single") -lt 2 -and (Get-Date) -lt $deadline) { Start-Sleep 1 }
        $t = Get-Date; Command "autofocus"
        $req = InfoAfter "Autofocus requested*" $t
        $af = $null; $deadline = (Get-Date).AddSeconds(60); while (-not $af -and (Get-Date) -lt $deadline) { Start-Sleep 1; $af = InfoAfter "E2E Single: running the requested autofocus*" $t }
        Shot "51-manual-autofocus"
        $afDone = $null; $deadline = (Get-Date).AddSeconds(400); while (-not $afDone -and (Get-Date) -lt $deadline) { Start-Sleep 2; $afDone = InfoAfter "E2E Single: requested autofocus *" $t }
        $framesBetween = @(FrameStarts "E2E Single" | Where-Object { $req -and $af -and $_ -ge (EvTime $req) -and $_ -lt (EvTime $af) }).Count
        Check "Manual autofocus: runs before the next frame (at most the frame already running finishes first)" ($req -and $af -and $afDone -and $framesBetween -le 1) "$($afDone.evt.message); frames started between request and autofocus: $framesBetween"
        $deadline = (Get-Date).AddSeconds(30); while (@(FrameStarts "E2E Single" | Where-Object { $afDone -and $_ -ge (EvTime $afDone) }).Count -lt 1 -and (Get-Date) -lt $deadline) { Start-Sleep 1 }
        Check "Imaging continues after the autofocus" (@(FrameStarts "E2E Single" | Where-Object { $afDone -and $_ -ge (EvTime $afDone) }).Count -ge 1 -or (Get-Date) -gt $s1End)

        $e = Wait-Event (Phase "End" "finished") 400 "4 End (finished) at the end time"
        $w = Wait-Event (Phase "WaitingForNextNight") 180 "waiting after the only target"
        $starts = FrameEnds "E2E Single"
        $late = @($starts | Where-Object { $_ -gt $s1End }).Count
        $st = ShutDownState
        Check "One target ending before the night ends: stops at its end time, 4 End shuts everything down and waits for safe" ($late -eq 0 -and $st.ok -and (FramesOf "E2E Single") -lt 500) ("end {0:HH:mm:ss}, {1} frames, {2}" -f $s1End, $starts.Count, $st.text)
        Shot "52-single-target-ended"
        $t = Get-Date; Set-Safe $false
        NextPhase "WaitingForSafe" $t 60 | Out-Null
        $t = Get-Date; Set-Safe $true
        $n = NextPhase "WaitingForNextNight" $t 90 "Nothing to image"
        Start-Sleep -Seconds 5
        Check "Safe again after the only target ended: the equipment stays off" (@(PhaseEvents | Where-Object { $_.evt.phase -eq "Begin" -and (EvTime $_) -ge $t }).Count -eq 0) $n.evt.message
    } elseif ($Scenario -eq "pause") {
        Set-Safe $true
        Wait-Event (Phase "Imaging" "E2E Pause") 300 "imaging E2E Pause" | Out-Null
        $deadline = (Get-Date).AddSeconds(120); while ((FramesOf "E2E Pause") -lt 2 -and (Get-Date) -lt $deadline) { Start-Sleep 1 }

        # 1) Pause after this frame: the running frame finishes, then paused with everything on
        $t = Get-Date; Command "pause after frame"
        $p = NextPhase "Paused" $t 120
        Start-Sleep -Seconds 2
        $starts = (FrameStarts "E2E Pause").Count; $ends = FramesOf "E2E Pause"
        Command "report sequence"; Start-Sleep -Seconds 1
        $seq = InfoAfter "E2E: NINA sequence running=*" $t
        $tracking = Sim "telescope" "tracking"; $shutter = Sim "dome" "shutterstatus"; $sw = Switches
        Check "Pause after this frame: the frame finishes and counts, then the sequence pauses" ($starts -eq $ends) "frames started $starts, finished $ends"
        Check "While paused: NINA's sequence is stopped, tracking, dome and power stay on, no 4 End" ($seq.evt.message -like "*running=False paused=True*" -and "$tracking" -eq "True" -and "$shutter" -eq "0" -and ($sw[0..1] -join ",") -eq "1,1" -and -not (PhaseEvents | Where-Object { $_.evt.phase -eq "End" })) "$($seq.evt.message); tracking=$tracking shutter=$shutter switches=$($sw[0..1] -join ',')"
        Command "planner tab 0"; Start-Sleep -Seconds 1; Shot "61-paused-start-sequence-button"

        # 2) change the filter while paused, then Start sequence: same target, mount unmoved -> guiding restarts only
        $t = Get-Date; Command "edit exposure filter Green"; Command "start sequence"
        $r = NextPhase "Imaging" $t 120
        $deadline = (Get-Date).AddSeconds(90); $green = $null
        while (-not $green -and (Get-Date) -lt $deadline) { Start-Sleep 1; $green = Events | Where-Object { $_.evt.kind -eq "frame" -and $_.evt.filter -eq "Green" -and (EvTime $_) -ge $t } | Select-Object -First 1 }
        $begins = @(PhaseEvents | Where-Object { $_.evt.phase -eq "Begin" -and (EvTime $_) -ge $t }).Count
        Check "Start sequence, same target, mount not moved: no 1 Begin, no slew, guiding restarts and imaging continues" ($r.evt.message -like "Resume:*mount where it was*" -and $begins -eq 0) $r.evt.message
        Check "A filter changed while paused is used for the next frames" ([bool]$green) "first Green frame at $(if ($green) { EvTime $green })"
        Shot "62-resumed-same-target"

        # 3) Pause now during a frame: that frame is dropped
        $deadline = (Get-Date).AddSeconds(60); $t0 = Get-Date
        while ((Get-Date) -lt $deadline) { $last = Events | Select-Object -Last 1; if ($last.evt.message -like "Frame starting: E2E Pause*") { break }; Start-Sleep -Milliseconds 100 }
        $t = Get-Date; Command "pause now"
        NextPhase "Paused" $t 60 | Out-Null
        Start-Sleep -Seconds 3
        $starts = (FrameStarts "E2E Pause").Count; $ends = FramesOf "E2E Pause"
        Check "Pause now: the frame being taken is dropped and not counted" ($starts -eq $ends + 1) "frames started $starts, finished $ends"

        # 4) the mount was moved while paused (e.g. to focus on a bright star): Start sequence slews back first
        Command "slew away"; Start-Sleep -Seconds 15
        $t = Get-Date; Command "start sequence"
        $r = NextPhase "Imaging" $t 120 "2 Start of target"
        $moved = InfoAfter "Resume: the mount moved*" $t
        $deadline = (Get-Date).AddSeconds(90); while ((FramesOf "E2E Pause") -le $ends -and (Get-Date) -lt $deadline) { Start-Sleep 1 }
        $pos = MountAt $raA 60
        Check "Start sequence after the mount moved: 2 Start of target slews back, then imaging" ($moved -and $pos.ok -and (FramesOf "E2E Pause") -gt $ends) "$($moved.evt.message); $($pos.text)"

        # 5) unsafe while paused: 4 End, then when safe 1 Begin, and it stays paused
        $t = Get-Date; Command "pause after frame"; NextPhase "Paused" $t 120 | Out-Null
        $framesPaused = FramesOf "E2E Pause"
        $t = Get-Date; Set-Safe $false
        $e = NextPhase "End" $t 60 "unsafe while paused"
        $w = NextPhase "WaitingForSafe" (EvTime $e) 180
        $st = ShutDownState
        Shot "63-unsafe-while-paused"
        $t = Get-Date; Set-Safe $true
        $b = NextPhase "Begin" $t 120 "still paused"
        $p = NextPhase "Paused" (EvTime $b) 120
        Start-Sleep -Seconds 5
        Check "Unsafe while paused: 4 End shuts down; safe again: 1 Begin powers up and it stays paused (no imaging)" ($st.ok -and (FramesOf "E2E Pause") -eq $framesPaused) "$($st.text); $($p.evt.message)"

        # 6) a device dropped while paused: Start sequence reconnects it
        Command "disconnect camera"; Start-Sleep -Seconds 5
        $t = Get-Date; Command "start sequence"
        $r = NextPhase "Imaging" $t 180
        $re = InfoAfter "Resume: Camera is not connected*" $t
        $deadline = (Get-Date).AddSeconds(120); while ((FramesOf "E2E Pause") -le $framesPaused -and (Get-Date) -lt $deadline) { Start-Sleep 1 }
        Check "Start sequence with the camera disconnected: it reconnects the camera and continues" ($re -and (FramesOf "E2E Pause") -gt $framesPaused) $re.evt.message

        # 7) a different target first while paused: Start sequence starts that target with 2 Start of target
        $t = Get-Date; Command "pause after frame"; NextPhase "Paused" $t 120 | Out-Null
        $t = Get-Date; Command "move target 2 first"; Start-Sleep -Seconds 2; Command "start sequence"
        $r = NextPhase "Imaging" $t 180 "2 Start of target: E2E Second"
        Check "Start sequence when another target is now first: that target starts with 2 Start of target" ([bool]$r) $r.evt.message
        Shot "64-resumed-other-target"

        # 8) Stop while imaging: 4 End runs and the run ends
        $deadline = (Get-Date).AddSeconds(60); while ((FramesOf "E2E Second") -lt 1 -and (Get-Date) -lt $deadline) { Start-Sleep 1 }
        $t = Get-Date; Command "stop planner"
        $e = NextPhase "End" $t 60 "stopped"
        $s = NextPhase "Stopped" (EvTime $e) 180
        $st = ShutDownState
        Command "report sequence"; Start-Sleep -Seconds 1
        $seq = InfoAfter "E2E: NINA sequence running=*" $t
        Check "Stop: 4 End (stopped) shuts down and the run ends" ($st.ok -and $seq.evt.message -like "*running=False paused=False*") "$($st.text); $($seq.evt.message)"
        Shot "65-stopped"
    } elseif ($Scenario -eq "restart") {
        Set-Safe $true
        Wait-Event (Phase "Imaging" "E2E Restart") 300 "imaging" | Out-Null
        $deadline = (Get-Date).AddSeconds(120); while ((FramesOf "E2E Restart") -lt 2 -and (Get-Date) -lt $deadline) { Start-Sleep 1 }
        $t = Get-Date; Command "pause after frame"; NextPhase "Paused" $t 120 | Out-Null
        Command "edit stage 1"; Start-Sleep -Seconds 8   # the workflow is saved automatically within a few seconds
        $framesBefore = (Get-Content $listPath -Raw | ConvertFrom-Json).Targets[0].Exposures[0].Done
        $profileDir = Join-Path $root "Profiles\$profileId"
        $wf = Get-ChildItem (Join-Path $profileDir "Workflows") -Filter *.json -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
        $state = Get-Content (Join-Path $profileDir "state.json") -Raw -ErrorAction SilentlyContinue | ConvertFrom-Json
        Check "The planner keeps this profile's data in its own folder (options, workflow, paused state)" ((Test-Path (Join-Path $profileDir "options.json")) -and $wf -and $state.Paused) "$profileDir; workflow $($wf.Name)"
        Check "Edits to the workflow are saved automatically" ((Get-Content $wf.FullName -Raw) -match '"Time":\s*7') $wf.Name

        # close NINA as if the power went off, then start it the normal way
        Stop-Process -Id $nina.Id -Force; [void]$nina.WaitForExit(30000); Say "NINA closed while paused"
        Remove-Item (Join-Path $root "e2e-commands.txt") -ErrorAction SilentlyContinue   # a new NINA must not replay old commands
        $restore = [ordered]@{ Restore = $true } | ConvertTo-Json; $restorePath = Join-Path $run "scenario-restore.json"; $restore | Set-Content $restorePath -Encoding utf8
        $env:OBSERVATORY_PLANNER_E2E = $restorePath; $env:OBSERVATORY_PLANNER_ROOT = $root
        $script:nina = Start-Process $NinaExe -ArgumentList "--profileid $profileId --disable-hardware-acceleration" -PassThru; $nina = $script:nina
        Remove-Item Env:OBSERVATORY_PLANNER_E2E, Env:OBSERVATORY_PLANNER_ROOT
        Say "NINA started again (pid $($nina.Id))"
        $t = (Get-Date).AddSeconds(-5)
        $done = Wait-Event { param($e) $e.kind -eq "info" -and $e.message -eq "E2E: startup restore finished" } 240 "startup restore"
        Start-Sleep -Seconds 4
        Command "show panel"; Command "planner tab 1"; Start-Sleep -Seconds 4; Command "report stages"; Start-Sleep -Seconds 1
        $restored = InfoAfter "Workflow restored*" $t
        $s1 = InfoAfter "Panel stage 1:*" $t
        $paused = InfoAfter "NINA started while the sequence was paused*" $t
        Check "After a restart the last workflow is back in the Advanced Sequencer, with the edit" ($restored -and $s1.evt.message -like "*Wait for Time Span  · 7 s*") "$($restored.evt.message)"
        Check "After a restart while paused it comes back paused on the same target" ([bool]$paused) $paused.evt.message
        Command "planner tab 0"; Start-Sleep -Seconds 2; Shot "71-restarted-still-paused"

        $t = Get-Date; Command "start sequence"
        $r = NextPhase "Imaging" $t 240
        $deadline = (Get-Date).AddSeconds(120); while ((Get-Content $listPath -Raw | ConvertFrom-Json).Targets[0].Exposures[0].Done -le $framesBefore -and (Get-Date) -lt $deadline) { Start-Sleep 2 }
        $after = (Get-Content $listPath -Raw | ConvertFrom-Json).Targets[0].Exposures[0].Done
        Check "Start sequence after the restart continues the target from its saved progress" ($after -gt $framesBefore) "frames before $framesBefore, now $after ($($r.evt.message))"
        Shot "72-continued-after-restart"

        # Stop, turn on "Start the run when NINA starts", restart: the run starts by itself
        $t = Get-Date; Command "stop planner"; NextPhase "Stopped" $t 240 | Out-Null
        Command "set autostart on"; Start-Sleep -Seconds 2
        Stop-Process -Id $nina.Id -Force; [void]$nina.WaitForExit(30000)
        Remove-Item (Join-Path $root "e2e-commands.txt") -ErrorAction SilentlyContinue
        $env:OBSERVATORY_PLANNER_E2E = $restorePath; $env:OBSERVATORY_PLANNER_ROOT = $root
        $script:nina = Start-Process $NinaExe -ArgumentList "--profileid $profileId --disable-hardware-acceleration" -PassThru; $nina = $script:nina
        Remove-Item Env:OBSERVATORY_PLANNER_E2E, Env:OBSERVATORY_PLANNER_ROOT
        $t = Get-Date
        $auto = $null; $deadline = (Get-Date).AddSeconds(240); while (-not $auto -and (Get-Date) -lt $deadline) { Start-Sleep 1; $auto = InfoAfter "Starting the run because*" $t }
        $img = NextPhase "Imaging" $t 300
        Check "With 'Start the run when NINA starts' on, the run starts by itself after a restart" ($auto -and $img) $img.evt.message
        Command "show panel"; Start-Sleep -Seconds 3; Shot "73-auto-started"
    } else {
        Command "tab imaging"; Command "show panel"
        Wait-Event (Phase "Imaging" "E2E Target C") 300 "imaging Target C" | Out-Null
        Check "Run without safety: 1 Begin -> Target C without waiting for safe" $true
        Shot "09-no-safety-imaging"
        Wait-Event (Phase "Finished") 300 "finished" | Out-Null
        Check "Runs once: 4 End then Finished (stops)" $true
        Start-Sleep -Seconds 3
        Shot "10-no-safety-finished"
        $c = FramesOf "E2E Target C"; Check "Target C: 2 frames" ($c -eq 2) "$c frames"
    }
} catch {
    Check "Scenario completed" $false $_.Exception.Message
    Shot "99-failure"
} finally {
    Save-Results
    Get-ChildItem (Join-Path $root "Logs") -Filter *.jsonl -ErrorAction SilentlyContinue | Copy-Item -Destination $run
    if ($nina -and -not $nina.HasExited) { Stop-Process -Id $nina.Id -Force; [void]$nina.WaitForExit(30000); Say "NINA test instance closed" }
    if ($safetyProc -and -not $safetyProc.HasExited) { Stop-Process -Id $safetyProc.Id -Force }
    # NINA may hold the profile file for a moment after exiting; retry so the user's NINA never opens the test profile by default.
    $profileFile = Join-Path $env:LOCALAPPDATA "NINA\Profiles\$profileId.profile"
    for ($i = 0; $i -lt 20 -and (Test-Path $profileFile); $i++) { try { Remove-Item $profileFile -Force -ErrorAction Stop } catch { Start-Sleep -Milliseconds 500 } }
    Remove-Item "$profileFile.bkp" -Force -ErrorAction SilentlyContinue
    Say ("test profile removed: " + (-not (Test-Path $profileFile)) + ", backup removed: " + (-not (Test-Path "$profileFile.bkp")))
    $passed = @($results | Where-Object passed).Count
    Say "RESULT $passed / $($results.Count) checks passed"
}
