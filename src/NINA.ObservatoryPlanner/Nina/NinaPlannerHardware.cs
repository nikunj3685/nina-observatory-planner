using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces;
using NINA.ObservatoryPlanner.Core;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.SequenceItem.Dome;
using NINA.Sequencer.SequenceItem.Imaging;
using NINA.Sequencer.SequenceItem.Platesolving;
using NINA.Sequencer.SequenceItem.Telescope;
using NINA.Sequencer.Trigger;
using NINA.Sequencer.Utility;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.ObservatoryPlanner.Nina {

    /// <summary>
    /// An instruction failed and its error behaviour ("Skip to end of sequence instructions" or "Abort") stopped NINA's
    /// sequence, which also stops the planner block.
    /// </summary>
    internal sealed record ErrorStop(string Where, string Item, InstructionErrorBehavior Behavior, bool InEnd) {
        public string BehaviorText => Behavior == InstructionErrorBehavior.AbortOnError ? "Abort sequence" : "Skip to end of sequence instructions";
    }

    /// <summary>The engine's view of the observatory, implemented with NINA's own sequencer instructions.</summary>
    internal class NinaPlannerHardware : IPlannerHardware {
        public static readonly string[] Devices = { "Safety Monitor", "Switch", "Dome", "Weather", "Camera", "Filter Wheel", "Focuser", "Rotator", "Mount", "Guider", "Flat Panel" };

        private readonly ObservatoryPlannerContainer container;
        private readonly PlannerService planner;
        private readonly IProgress<ApplicationStatus> progress;
        private InstructionFactory F => planner.Factory;

        public NinaPlannerHardware(ObservatoryPlannerContainer container, PlannerService planner, IProgress<ApplicationStatus> progress) {
            this.container = container;
            this.planner = planner;
            this.progress = progress;
        }

        // a step the planner runs outside the sequence (connect, between-target steps); between-target steps are never paused
        private volatile ISequenceItem standalone;
        private volatile bool standaloneProtected;
        private volatile PlannerImagingRun imagingRun;

        private async Task RunStandalone(ISequenceItem item, CancellationToken token, bool protect = false) {
            standalone = item;
            standaloneProtected = protect;
            try {
                await item.Run(progress, token);
            } catch (OperationCanceledException) when (token.IsCancellationRequested) {
                throw;
            } catch (Exception ex) {
                Logger.Error($"Observatory Planner: {item.Name} failed", ex);
                planner.Log.Info($"{item.Name} failed: {ex.Message}");
            } finally {
                standalone = null;
                standaloneProtected = false;
            }
        }

        private IEnumerable<ISequenceItem> RunningItems() {
            try {
                return ItemUtility.GetRootContainer(container)?.GetCurrentRunningItems() ?? (IEnumerable<ISequenceItem>)Array.Empty<ISequenceItem>();
            } catch (Exception) { return Array.Empty<ISequenceItem>(); }
        }

        private IEnumerable<ISequenceTrigger> RunningTriggers() =>
            imagingRun?.GetTriggersSnapshot().Where(t => t.Status == SequenceEntityStatus.RUNNING) ?? Enumerable.Empty<ISequenceTrigger>();

        public object CurrentStep() =>
            (object)standalone ?? (object)RunningTriggers().FirstOrDefault() ?? RunningItems().FirstOrDefault(i => i is not TakeExposure && i is not ISequenceContainer);

        /// <summary>A meridian flip (NINA's or a plugin's flip trigger), the dome shutter, park, or a between-targets step.</summary>
        public bool InProtectedStep() =>
            (standalone != null && standaloneProtected)
            || RunningTriggers().Any(t => t.GetType().Name.Contains("MeridianFlip"))
            || RunningItems().Any(i => i is ParkScope || i is OpenDomeShutter || i is CloseDomeShutter);

        public int FinishedSteps(StageKind kind) {
            var stage = container.Stage(kind);
            if (stage == null) { return 0; }
            var done = 0;
            foreach (var item in stage.GetItemsSnapshot()) {
                if (item.Status is SequenceEntityStatus.CREATED or SequenceEntityStatus.RUNNING) { break; }
                done++;
            }
            return done;
        }

        public bool ImagingStarted(PlannerTarget target) => planner.ImagingTargetId == target.Id;

        public async Task ConnectKeptDevices(CancellationToken token) {
            foreach (var device in Devices.Where(planner.Options.Keeps)) {
                await RunStandalone(F.Connect(device), token);
            }
        }

        /// <summary>Set when an instruction's error behaviour stopped NINA's sequence during this run.</summary>
        public ErrorStop ErrorStop { get; private set; }

        public Task RunStage(StageKind kind, CancellationToken token) => ContinueStage(kind, 0, token);

        /// <summary>Runs the stage; its first <paramref name="done"/> steps ran before a pause and are marked finished.</summary>
        public async Task ContinueStage(StageKind kind, int done, CancellationToken token) {
            var stage = container.Stage(kind);
            if (stage == null || kind == StageKind.Triggers) { return; }
            await Ui.Run(() => {
                stage.ResetAll();
                foreach (var item in stage.GetItemsSnapshot().Take(done).Where(i => i.Status == SequenceEntityStatus.CREATED)) { item.Status = SequenceEntityStatus.FINISHED; }
            });
            if (kind == StageKind.End) { planner.SetEndProblems(null); planner.OnEndStarting(); }
            if (kind == StageKind.Begin) { planner.OnBeginStarting(); }
            try {
                await stage.Run(progress, token);
            } finally {
                NoteErrorStop(stage, PlannerStageContainer.Title(kind), kind == StageKind.End, token);
                if (kind == StageKind.End) { ReportEnd(stage); }
            }
        }

        internal static IEnumerable<ISequenceItem> Descendants(ISequenceContainer c) {
            foreach (var item in c.GetItemsSnapshot()) {
                yield return item;
                if (item is ISequenceContainer sub) { foreach (var d in Descendants(sub)) { yield return d; } }
            }
            foreach (var trigger in (c as ITriggerable)?.GetTriggersSnapshot().OfType<SequenceTrigger>() ?? Enumerable.Empty<SequenceTrigger>()) {
                if (trigger.TriggerRunner != null) { foreach (var d in Descendants(trigger.TriggerRunner)) { yield return d; } }
            }
        }

        /// <summary>After NINA's sequence was cancelled: remembers the failed instruction whose error behaviour did it, if any.</summary>
        private void NoteErrorStop(ISequenceContainer c, string where, bool inEnd, CancellationToken token) {
            if (!token.IsCancellationRequested || ErrorStop != null) { return; }
            ErrorStop = FindErrorStop(c, where, inEnd);
        }

        /// <summary>The failed instruction in <paramref name="c"/> whose error behaviour stops NINA's whole sequence, or null.</summary>
        internal static ErrorStop FindErrorStop(ISequenceContainer c, string where, bool inEnd) {
            var failed = Descendants(c).FirstOrDefault(i => i.Status == SequenceEntityStatus.FAILED
                && i.ErrorBehavior is InstructionErrorBehavior.SkipToSequenceEndInstructions or InstructionErrorBehavior.AbortOnError);
            return failed == null ? null : new ErrorStop(where, failed.Name, failed.ErrorBehavior, inEnd);
        }

        /// <summary>Failed 4 End steps are shown in the panel: a failed power-off or warm-up needs a look.</summary>
        private void ReportEnd(PlannerStageContainer stage) {
            var failed = Descendants(stage).Where(i => i.Status == SequenceEntityStatus.FAILED && i is not ISequenceContainer).Select(i => i.Name).ToList();
            if (failed.Count == 0) { return; }
            var text = $"4 End: {failed.Count} step{(failed.Count == 1 ? "" : "s")} failed: {string.Join(", ", failed)}. Check the equipment; NINA's log has the details.";
            planner.Log.Info(text);
            planner.AddEndProblem(text);
        }

        private ISequenceItem GapItem(GapStep step) => step switch {
            GapStep.StopGuiding => F.StopGuiding(),
            GapStep.StopTracking => F.SetTracking(TrackingMode.Stopped),
            GapStep.Park => F.Park(),
            GapStep.FindHome => F.FindHome(),
            GapStep.CloseDome => F.CloseDome(),
            GapStep.OpenDome => F.OpenDome(),
            _ => F.Unpark()
        };

        public async Task RunGapSteps(IReadOnlyList<GapStep> steps, CancellationToken token) {
            foreach (var step in steps) {
                planner.Log.Info($"Between targets: {step}");
                await RunStandalone(GapItem(step), token, protect: true);
            }
        }

        private bool DomeConnected() => planner.Nina.Dome.GetInfo()?.Connected == true;

        /// <summary>Runs the steps; false when one of <paramref name="mustWork"/> failed.</summary>
        private async Task<bool> RunWeatherSteps(string what, IEnumerable<GapStep> steps, ISet<GapStep> mustWork, CancellationToken token) {
            var ok = true;
            foreach (var step in steps) {
                planner.Log.Info($"{what}: {step}");
                var item = GapItem(step);
                await RunStandalone(item, token, protect: true);
                if (mustWork.Contains(step) && item.Status != SequenceEntityStatus.FINISHED) {
                    planner.Log.Info($"{what}: {step} failed");
                    ok = false;
                }
            }
            return ok;
        }

        public Task<bool> CloseUp(CancellationToken token) {
            var steps = new List<GapStep> { GapStep.StopGuiding, GapStep.StopTracking, GapStep.Park };
            if (DomeConnected()) { steps.Add(GapStep.CloseDome); }
            return RunWeatherSteps("Closing up for the weather", steps, new HashSet<GapStep> { GapStep.Park, GapStep.CloseDome }, token);
        }

        public Task<bool> Reopen(CancellationToken token) {
            planner.OnBeginStarting(); // autofocus before the first frame, as after 1 Begin
            var steps = new List<GapStep>();
            if (DomeConnected()) { steps.Add(GapStep.OpenDome); }
            steps.Add(GapStep.Unpark);
            return RunWeatherSteps("Opening up after the weather", steps, new HashSet<GapStep> { GapStep.OpenDome, GapStep.Unpark }, token);
        }

        /// <summary>Devices imaging needs, as named by Connect Equipment, with whether the profile has one set up.</summary>
        private IEnumerable<(string Device, bool Configured, Func<bool> Connected)> ImagingDevices() {
            var p = planner.Nina.Profile.ActiveProfile;
            static bool Set(string id) => !string.IsNullOrWhiteSpace(id) && id != "No_Device";
            yield return ("Camera", Set(p.CameraSettings.Id), () => planner.Nina.Camera.GetInfo()?.Connected == true);
            yield return ("Mount", Set(p.TelescopeSettings.Id), () => planner.Nina.Telescope.GetInfo()?.Connected == true);
            yield return ("Filter Wheel", Set(p.FilterWheelSettings.Id), () => planner.Nina.FilterWheel.GetInfo()?.Connected == true);
            yield return ("Focuser", Set(p.FocuserSettings.Id), () => planner.Nina.Focuser.GetInfo()?.Connected == true);
            yield return ("Guider", Set(p.GuiderSettings.GuiderName) && p.GuiderSettings.GuiderName != "No_Guider", () => planner.Nina.Guider.GetInfo()?.Connected == true);
        }

        public async Task<IReadOnlyList<string>> CheckConnections(CancellationToken token) {
            var missing = new List<string>();
            foreach (var (device, configured, connected) in ImagingDevices()) {
                if (!configured || connected()) { continue; }
                planner.Log.Info($"Resume: {device} is not connected; trying to connect it");
                await RunStandalone(F.Connect(device), token);
                if (!connected()) { missing.Add(device); planner.Log.Info($"Resume: {device} could not be connected"); }
            }
            return missing;
        }

        public (double RaHours, double DecDeg)? MountPosition() {
            try {
                var info = planner.Nina.Telescope.GetInfo();
                if (info?.Connected != true || info.Coordinates == null) { return null; }
                var c = info.Coordinates.Transform(NINA.Astrometry.Epoch.J2000);
                return (c.RA, c.Dec);
            } catch (Exception) { return null; }
        }

        public async Task StopGuiding(CancellationToken token) {
            if (planner.Nina.Guider.GetInfo()?.Connected == true) { await RunStandalone(F.StopGuiding(), token); }
        }

        /// <summary>The instruction that moves to the target, from the target's "When target starts" setting.</summary>
        private ISequenceItem MoveItem(PlannerTarget t) => t.OnStart switch {
            OnStartAction.SlewAndCenter => t.Rotate ? F.CenterAndRotate(t.PositionAngle) : F.Center(),
            OnStartAction.SlewOnly => F.SlewToTarget(),
            _ => null
        };

        private static bool IsMove(ISequenceItem i) => i is Center || i is SlewScopeToRaDec; // CenterAndRotate derives from Center

        public async Task<int> RunTarget(PlannerTarget target, Func<PlannerExposure, bool> canStartFrame, CancellationToken token) {
            planner.ImagingTargetId = null;
            var dso = F.TargetContainer(target);

            // 2 Start of target, adapted to this target: its "When target starts" choice replaces the stage's move step.
            var stageItems = container.Stage(StageKind.TargetStart)?.Items.ToList() ?? new List<ISequenceItem>();
            var moved = false;
            foreach (var item in stageItems) {
                if (IsMove(item)) {
                    if (!moved) {
                        var move = MoveItem(target);
                        if (move != null) { move.Attempts = item.Attempts; move.ErrorBehavior = item.ErrorBehavior; dso.Add(move); }
                        moved = true;
                    }
                    continue;
                }
                dso.Add((ISequenceItem)item.Clone());
            }
            if (!moved) {
                var move = MoveItem(target);
                if (move != null) { dso.InsertIntoSequenceBlocks(0, move); }
            }
            return await RunImaging(dso, target, canStartFrame, token);
        }

        /// <summary>Resume on the same target, mount where it was: no slew or centering, just tracking on and guiding restarted.</summary>
        public async Task<int> ResumeTarget(PlannerTarget target, Func<PlannerExposure, bool> canStartFrame, CancellationToken token) {
            planner.ImagingTargetId = null;
            var dso = F.TargetContainer(target);
            dso.Add(F.SetTracking(TrackingMode.Sidereal));
            if (planner.Nina.Guider.GetInfo()?.Connected == true) { dso.Add(F.StartGuiding(forceCalibration: false)); }
            return await RunImaging(dso, target, canStartFrame, token);
        }

        private async Task<int> RunImaging(NINA.Sequencer.Container.DeepSkyObjectContainer dso, PlannerTarget target, Func<PlannerExposure, bool> canStartFrame, CancellationToken token) {
            if (target.DelayFirst > 0) { dso.Add(F.WaitSeconds(target.DelayFirst)); }

            var run = new PlannerImagingRun(planner, target, canStartFrame);
            imagingRun = run;
            foreach (var trigger in container.Stage(StageKind.Triggers)?.Triggers.ToList() ?? new List<ISequenceTrigger>()) {
                run.Add((ISequenceTrigger)trigger.Clone());
            }
            dso.Add(run);

            planner.TakeGuideStarGaveUp();
            await Ui.Run(() => container.Add(dso));
            try {
                await dso.Run(progress, token);
            } finally {
                NoteErrorStop(dso, $"{target.Name} (2 Start of target or imaging)", inEnd: false, token);
                imagingRun = null;
                await Ui.Run(() => container.Remove(dso));
            }
            if (planner.TakeGuideStarGaveUp()) { throw new GuideStarLostException(target.Name); }
            return run.FramesTaken;
        }
    }

    internal class NinaSafetySource : ISafetySource {
        private readonly NinaServices nina;
        public NinaSafetySource(NinaServices nina) { this.nina = nina; }

        public SafetyState Read() {
            var info = nina.SafetyMonitor.GetInfo();
            return new SafetyState(info?.Connected == true, info?.IsSafe == true);
        }
    }
}
