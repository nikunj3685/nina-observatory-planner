using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces;
using NINA.ObservatoryPlanner.Core;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.SequenceItem.Platesolving;
using NINA.Sequencer.SequenceItem.Telescope;
using NINA.Sequencer.Trigger;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.ObservatoryPlanner.Nina {

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

        private async Task RunStandalone(ISequenceItem item, CancellationToken token) {
            try {
                await item.Run(progress, token);
            } catch (OperationCanceledException) when (token.IsCancellationRequested) {
                throw;
            } catch (Exception ex) {
                Logger.Error($"Observatory Planner: {item.Name} failed", ex);
                planner.Log.Info($"{item.Name} failed: {ex.Message}");
            }
        }

        public async Task ConnectKeptDevices(CancellationToken token) {
            foreach (var device in Devices.Where(planner.Options.Keeps)) {
                await RunStandalone(F.Connect(device), token);
            }
        }

        public async Task RunStage(StageKind kind, CancellationToken token) {
            var stage = container.Stage(kind);
            if (stage == null || kind == StageKind.Triggers) { return; }
            await Ui.Run(() => stage.ResetAll());
            await stage.Run(progress, token);
        }

        public async Task RunGapSteps(IReadOnlyList<GapStep> steps, CancellationToken token) {
            foreach (var step in steps) {
                planner.Log.Info($"Between targets: {step}");
                var item = step switch {
                    GapStep.StopGuiding => F.StopGuiding(),
                    GapStep.StopTracking => F.SetTracking(TrackingMode.Stopped),
                    GapStep.Park => F.Park(),
                    GapStep.FindHome => F.FindHome(),
                    GapStep.CloseDome => F.CloseDome(),
                    GapStep.OpenDome => F.OpenDome(),
                    _ => F.Unpark()
                };
                await RunStandalone(item, token);
            }
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
            var dso = F.TargetContainer(target);
            dso.Add(F.SetTracking(TrackingMode.Sidereal));
            if (planner.Nina.Guider.GetInfo()?.Connected == true) { dso.Add(F.StartGuiding(forceCalibration: false)); }
            return await RunImaging(dso, target, canStartFrame, token);
        }

        private async Task<int> RunImaging(NINA.Sequencer.Container.DeepSkyObjectContainer dso, PlannerTarget target, Func<PlannerExposure, bool> canStartFrame, CancellationToken token) {
            if (target.DelayFirst > 0) { dso.Add(F.WaitSeconds(target.DelayFirst)); }

            var run = new PlannerImagingRun(planner, target, canStartFrame);
            foreach (var trigger in container.Stage(StageKind.Triggers)?.Triggers.ToList() ?? new List<ISequenceTrigger>()) {
                run.Add((ISequenceTrigger)trigger.Clone());
            }
            dso.Add(run);

            await Ui.Run(() => container.Add(dso));
            try {
                await dso.Run(progress, token);
            } finally {
                await Ui.Run(() => container.Remove(dso));
            }
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
