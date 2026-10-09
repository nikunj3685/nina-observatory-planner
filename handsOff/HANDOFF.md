# Handoff notes — Observatory Planner session (last updated 2026-10-07)

This file exists so a **new Claude session or maintainer** can pick up this
codebase without any memory of the conversation that produced it. Read it top
to bottom before changing the plugin.

## What this project is

Observatory Planner is a N.I.N.A. 3.2 plugin (.NET 8, WPF, MEF) that runs an
observatory unattended, SGP style. Repo: `nikunj3685/nina-observatory-planner`
(public, MPL-2.0). The maintainer is Nikunj Patel, who is moving from SGP 3.2 to
NINA.

- **With a safety monitor:** wait for safe → 1 Begin → targets (2 Start of
  target, imaging with 3 triggers) → 4 End when unsafe or finished → wait for
  safe → resume, forever.
- **Without one:** one pass through the targets from astronomical dusk.
- The four stages are plain Advanced Sequencer instruction sets inside one
  "Observatory Planner" block; the panel (Imaging tab) edits targets and options.

## Working rules agreed with the user

- **Commit or push only when asked.** Confirm anything outward-facing (pushes,
  tags, releases, PRs).
- **The NINA plugin-list PR is on hold.** The user is testing on real nights and
  must review the code personally before submitting; don't open it.
- **After a change: unit tests + install only.** No full simulator (e2e) runs
  unless the user asks; the user does the rig testing.
- **Design items:** agree the design with the user first, one question at a
  time, before coding.
- **Don't modify the user's NINA profile, don't close their NINA** (rename a
  locked DLL aside instead) and don't click security dialogs for them.
- Commit trailer: `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Git and release state

- The 1.0.0.0 release (tag `1.0.0.0`) is the published one. The work below was
  committed on 2026-10-08 on branch `improvements-from-test-nights`, merged into
  `main` and pushed. **No new version, tag or release** (the user asked to keep
  1.0.0.0): the csproj stays at 1.0.0.0 and the CHANGELOG section is
  "Unreleased". `Feature-Improvement List.txt` (the user's own list) is not
  committed.
- The manifest for 1.0.0.0 is on branch `ObservatoryPlanner/1.0.0.0` of the fork
  `nikunj3685/nina.plugin.manifests`
  (`manifests/o/ObservatoryPlanner/3.2.0/manifest.json`). The PR is **not** opened.
- **To release later:** set the version in the csproj and the CHANGELOG heading,
  retake `docs/images` (still 1.0: Gain, Count, Pause in the target header),
  tag it, push the tag, then replace the manifest on a new fork branch and
  validate it with `node gather.js`. The steps are in `PUBLISHING.md`.

## Test results (2026-10-08)

- **Unit tests:** 194, all passing.
- **Simulator e2e:** 78/78 (nosafety 4, safety 22, ui 11, negative 11, timing 7,
  single 5, pause 11, restart 7).
- **Fixed during this round:**
  - "Close PHD2 on disconnect" would also close a PHD2 open next to NINA when
    NINA's guider is not PHD2. It now checks that GuiderName starts with
    "PHD2". The e2e scenarios turn both close options off, because the user's
    PHD2 may be running.
  - "Pause after this step" during a dither started a frame and aborted it. The
    imaging loop now checks `FrameAllowed` again right before the frame.
  - The autofocus log wording is back to "requested autofocus finished"
    (the e2e check uses it).
- **"GS Server connects but doesn't slew"** was reproduced on GS Server's own
  simulator: it starts parked and refuses slews ("Telescope parked"). Fix:
  Slew now / Center now offer to unpark, and the 1 Begin check warns when
  nothing unparks. The user will confirm on the live rig. Releasing the ASCOM
  object made GS Server exit by itself (confirms the #14 design).

## Build, test, install

Only the .NET 10 runtime is installed on this PC, so set roll-forward:

```bash
export DOTNET_ROLL_FORWARD=LatestMajor
dotnet test NINA.ObservatoryPlanner.slnx                     # 194 tests, all passing
dotnet build src/NINA.ObservatoryPlanner/NINA.ObservatoryPlanner.csproj -c Release
```

**Install:** copy `NINA.ObservatoryPlanner.dll` and `.pdb` from
`src/NINA.ObservatoryPlanner/bin/Release/net8.0-windows/` to
`%LOCALAPPDATA%\NINA\Plugins\3.0.0\Observatory Planner\`. If NINA is running,
rename the locked DLL to `*.old` and copy the new one; NINA loads it at its next
start. The 1.1.0.0 Release build is installed there now.

**E2E (simulators, only when asked):**
`powershell -File tools\e2e\run_e2e.ps1 -Scenario <safety|nosafety|ui|negative|timing|single|pause|restart>`.
It needs ASCOM OmniSim (Alpaca on localhost:32323) and Python 3. The scenario
options turn "Autofocus after 1 Begin" off and remove both AF triggers, because
the simulated images have no stars.

**NINA 3.2 source** for reading: the NINA repo at
`C:\Users\nikun\Documents\projects\nina`, via `git show 2393eae58:<path>`.

## Where things are

| Area | Files |
| --- | --- |
| Night loop (pause, resume, stop, safety watchdog, countdowns) | `src/.../Core/PlannerEngine.cs` |
| Model, options, JSON | `Core/Models.cs`, `Core/PlannerStore.cs` (per-profile data under `Documents\N.I.N.A\Observatory Planner\Profiles\<id>`) |
| Stage warnings | `Core/StageChecks.cs` |
| NINA side: running stages and targets, error stops, End report | `Nina/NinaPlannerHardware.cs` |
| Planner block, stage containers, imaging loop, planner AF trigger | `Nina/SequencerBlocks.cs` |
| Shared state, startup restore, pause watch, autofocus rules | `Nina/PlannerService.cs` |
| Built-in workflows (the user's switch names) | `Nina/WorkflowLibrary.cs` |
| Panel and dialogs | `UI/PlannerView.xaml`, `UI/PlannerDockableVM.cs`, `UI/Dialogs/*` |
| Simulator test hook (DEBUG builds only) | `Nina/E2EHook.cs` |

## Pitfalls already found (don't rediscover them)

- NINA composes the plugin manifest and the other parts in **two MEF
  containers**; `PlannerServiceExport` keeps one static `PlannerService`.
- NINA creates plugin VMs on a background thread: `DispatcherTimer`s must use
  `Application.Current.Dispatcher`.
- **Running instructions:**
  - NINA only runs instructions with status CREATED: `StartRun` resets the root
    first.
  - `DisconnectEquipment` is internal and is created by reflection.
  - NINA won't slew a parked mount, so 1 Begin ends with Unpark.
- **Error behaviour "Skip to end of sequence instructions" / "Abort"** cancel
  NINA's whole sequence like its Stop button. The planner now detects the failed
  instruction (`ErrorStop`) and runs 4 End itself.
- **NINA's Sequencer tab** opens on its overview page after startup;
  `PlannerService.ShowAdvancedSequencerAsync` switches it to the Advanced
  Sequencer 2 s after `Initialized`.
- **Framing Assistant:** the position angle is on `CameraRectangles`;
  `framing.Rectangle.DSOPositionAngle` is never set.
- **NINA 3 has no DARKFLAT image type** (it migrates DARKFLAT to DARK).
- **NINA's "AF After Filter Change"** compares with the filter of the last
  autofocus (from image history) when no AF filter is set in the profile; the
  planner has its own trigger for that reason.
- **Styling:** NINA's CheckBox is an ON/OFF switch without a label (use the
  `OP_Check` style); implicit style overrides need `BasedOn`.
- **Bash heredocs** for long edit scripts break here; write the script to the
  scratchpad and run it.

## The user's rig (from the logs)

- **Power switches on the hub:** ZWO 2600 MM P, ZWO 2600 MM, ZWO EAF P,
  ZWO EAF, SW CQ-350, ZWO 290 MM, Filter Wheel. Also a GS Server mount, PHD2,
  and an Alpaca dome and safety monitor at 192.168.2.119/120.
- **Their own 4 End** (6 Oct) had all the switch power-off steps **disabled** and
  used `Documents\N.I.N.A\DissConnectScript.bat` instead. The script only closes
  PHD2 and GS Server, and it ended with an error. That is why the camera power
  stayed on. They were told; it's their workflow to fix.
- **Their stage 3** still has NINA's "AF After Filter Change". The panel now warns
  about it; they need to swap in "AF after filter change (planner)".
- **Logs:** NINA's in `%LOCALAPPDATA%\NINA\Logs`; the planner's (JSON lines) in
  `Documents\N.I.N.A\Observatory Planner\Logs`.

## What changed this session (1.1.0.0, from `Feature-Improvement List.txt`)

Done, unit-tested (194 passing) and installed, not committed or tested on the rig:

- **#1** Countdown during the wait after safe, and while waiting for the next target.
- **#2** 4 End always runs when a step's error behaviour stops NINA's sequence. Failed
  4 End steps are shown under the status bar. Built-in 1 Begin connects the
  switch hub first, with a stage warning when Set Switch Value comes before the
  hub is connected.
- **#3** Gain column removed; the camera's gain is used. 1.0 lists still load.
- **#4** Exposure type per row: Light, Dark, Bias, Flat, Dark flat (saved as DARK).
- **#6** ▶ marks the exposure row being imaged.
- **#7** Pause is disabled while 4 End runs.
- **#10** The last workflow is shown in the Advanced Sequencer at startup.
- **#12** Targets start at their set time (the hidden 5-minute early wake-up is gone).
- **#13** Target Settings: altitude and time are both editable and follow each other.
- **#16** Rotation from the Framing Assistant is taken.
- **#17** Autofocus:
  - Option "Autofocus before the first frame after 1 Begin" (on by default).
  - New stage 3 trigger "AF after filter change (planner)": compares with the
    previous light frame's filter, across targets, pauses and weather stops,
    and never autofocuses twice for one frame.
  - It replaces NINA's trigger in the built-in workflows.
- **#5** Guide star lost (⚙ Options, `Core/GuiderWatch.cs`, imaging loop in
  `Nina/SequencerBlocks.cs`): reads PHD2's state through
  `IGuiderMediator.GetDevice()` (`IGuider.State == "LostLock"`). A loss under
  10 s is ignored; a longer one aborts the frame (not counted) and waits up to
  N s (default 60) from the loss. Not found → `GuideStarLostException` → the
  engine either stops for the night (default: 4 End, then waits until local
  noon) or skips the target for tonight. Weather during the wait cancels it
  as usual.

- **#18** Guiding error check (⚙ Options, off by default; `GuideErrorWindow` and
  `GuiderWatch` in `Core/GuiderWatch.cs`): RMS of √(RA²+Dec²) over the last 10
  PHD2 guide steps in guide camera pixels, from `IGuiderMediator.GuideEvent`
  (cleared after a dither or a guiding start; unknown with under 3 steps in
  60 s). Before a light frame: wait up to 120 s for it to go below the limit,
  then start anyway. During a frame: above the limit for 10 s → the frame is
  dropped and retaken, except a frame that started because the wait ran out.

- **#14** Close PHD2 and the mount software on disconnect (⚙ Options › When
  disconnecting; `Nina/DisconnectCloser.cs`). Hooked into NINA's
  `IGuiderMediator.Disconnected` / `ITelescopeMediator.Disconnected`, which NINA
  awaits, so it works with any Disconnect Equipment / Disconnect All, wherever
  it is, while the Advanced Sequencer runs. PHD2: JSON-RPC to
  PHD2ServerUrl:PHD2ServerPort (`stop_capture`, `set_connected [false]`,
  `shutdown`). Mount program: the ASCOM local server from the registry
  (ProgID → CLSID → LocalServer32, 64- then 32-bit view; on this PC
  `...\ASCOM\Telescope\GSServer\GS.Server.exe`). It exits by itself once no
  client is left; still open after 30 s → left open, note shown (the user
  agreed not to force-close). PHD2 always goes first, because it holds GS
  Server open. The user's old script killed `GS_Server.exe`, which is the
  wrong name.

- **#11** One Pause for the whole run, in the status bar. Engine:
  `PauseAllowed` = `nightActive` (from the start of 1 Begin) and not in 4 End.
  Pause now cancels at once, or after `InProtectedStep()` clears (a flip
  trigger RUNNING, Park, dome shutter, standalone between-target steps). Pause
  after this step waits for `CurrentStep()` to change. Between targets the wait
  loop handles it, with no cancel, so a park is undone first. 1 Begin progress
  is kept in `PausePoint.BeginDone` (leading finished top-level items) →
  `ContinueStage`. `PausePoint.ImagingStarted` (2 Start of target finished)
  must be true for the light resume. `ResumeChange` explains a target switch.
  Persisted in state.json (`BeginDone`, `ImagingStarted`; missing in 1.0
  files → false).
- **Weather close-up** (new request, 2026-10-08; ⚙ Options › Weather,
  `UnsafeAction` RunEnd by default / CloseUpAndWait, `CloseUpMaxHours` 2). On
  unsafe after 1 Begin has finished, `ShutForWeather` in `RunNight` calls
  `hardware.CloseUp` (StopGuiding, StopTracking, Park, CloseDome if a dome is
  connected; false if park or dome fails → 4 End) → `NightResult.ClosedUp` →
  `WaitClosedUp` (phase `ClosedUp`). Safe + wait after safe → `RunNight(reopen)`:
  `hardware.Reopen` (OpenDome, Unpark, and sets the after-Begin autofocus) →
  targets, no 1 Begin. 4 End on the limit, `Morning()` (before noon and the sun
  above DarkSunAltitude), nothing left tonight, or Stop; after morning or
  nothing left, it waits until local noon. Pause is not allowed while closed
  up (use Stop).
- **"Night: image only while the Sun is below"** (`DarkSunAltitude`) moved from
  "Defaults for new targets" to the top of ⚙ Options › Weather, which is now always
  shown (the close-up part only with safety), with a full note. It still drives
  target selection (`TargetSelector.IsDark`) and the close-up's `Morning()`. The
  user considered removing it from target selection, but kept it, because it is
  the only rule that stops imaging at dawn.
- **Target list icons** (user, 2026-10-08, "keep it simple": no Waiting /
  Not tonight / Skipped states): `TargetStateConverter.StateOf` with
  `PlannerDockableVM.ListState` (imaging and paused target ids).
  ▶ Imaging, ‖ Paused (grey when checked and idle, orange when the run is
  paused on it), ■ Stopped (unchecked, or TotalFrames 0), ✔ Complete.
  The tooltip gives the word.
- **4 End check** is device-aware (`StageChecks.DevicesOf`): it warns only when a
  step needs a device already disconnected earlier in the stage, or anything after
  Disconnect All. The user declined an option to ignore NINA's "AF After Filter
  Change" (2026-10-08): the stage 3 warning stays, and they swap it by hand.
- **Info tab** (the ! next to the ⚙ gear, `SelectedPlannerTab` 3): notes on
  editing during a run and on adding sections in the Advanced Sequencer.

**Not verified on a real NINA yet:** 4 End after an error stop, the
Sequencer-tab switch at startup, rotation from Framing, the autofocus timing,
the guide-star-lost and guiding-error handling with a real PHD2, and closing
PHD2 and GS Server on disconnect, and the global pause (especially during a
real meridian flip, dome opening and 1 Begin), and the weather close-up
on a real cloudy night.

## Waiting for the user's decisions

| # | Item | Open question |
| --- | --- | --- |
| 8 | Drag and drop for targets and exposures | **Deferred by the user on 2026-10-08**: keep it on the to-do list, don't build it yet. Open question when it comes back: no confirmation (drag back to undo), or ask each time? |
| 9 | Sections added in the Advanced Sequencer (e.g. flats) | **On the to-do list (user, 2026-10-08).** Findings: (1) **Bug, data loss:** `ObservatoryPlannerContainer.Execute` removes every item in the block that isn't a `PlannerStageContainer` (meant for leftover target blocks), so a user's section placed between the stages is deleted at run start and autosaved without it. Fix: tag the planner's own target blocks and remove only those. (2) A second "Planner stage" defaults to stage 1 and is ignored (`Stage()` takes the first). (3) A section inside a stage works, but runs every time that stage runs (4 End also on unsafe); panel lines and stage checks see top-level items only. (4) Anything after the block or in NINA's End area runs only after the planner stops, when the equipment is already off. Proposed: the fix, warnings for (2) and (4), and for flats either (a) a 5th stage "End of night", run only when the night is finished, before 4 End (recommended), or (b) flats inside 4 End. Until fixed, tell users to put sections only inside a stage. |
| 14 | GS Server "connects but doesn't slew" | Reproduced and fixed (parked mount, see Test results). Waiting for the user's confirmation on the live rig. |
| 15 | Stage edits during a run | **Kept as it is (user, 2026-10-08)**, explained in the new Info tab (the ! next to the gear). Stages 2 and 3 are copied at each target start (and stage 3 again on resume), so edits apply from the next target. The temporary target block shown in the Advanced Sequencer holds copies, and edits there are lost when the target ends. Proposed but not wanted for now: mark and lock that block, or apply stage 3 trigger edits from the next frame. |

Item 19 in the list is empty.
