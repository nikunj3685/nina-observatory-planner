using NINA.Core.Model;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.ObservatoryPlanner.Core;
using NINA.Sequencer.SequenceItem;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.ObservatoryPlanner.Nina {

    /// <summary>
    /// Turns the camera rotator to the target's position angle without plate solving (for "Slew only" and "Don't move"; with
    /// "Slew and center" NINA's Center and Rotate does it). Skipped when no rotator is connected. Not in NINA's instruction list.
    /// </summary>
    internal class PlannerRotate : SequenceItem {
        private readonly IRotatorMediator rotator;
        private readonly IPlannerLog log;

        public PlannerRotate(IRotatorMediator rotator, IPlannerLog log, double positionAngle) {
            this.rotator = rotator;
            this.log = log;
            PositionAngle = positionAngle;
            Name = $"Rotate camera to {positionAngle:0.#}°";
            Category = "Rotator";
        }

        public double PositionAngle { get; }

        public override object Clone() => new PlannerRotate(rotator, log, PositionAngle);

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            var info = rotator.GetInfo();
            if (info?.Connected != true) {
                log.Info($"Rotation to {PositionAngle:0.#}° skipped: no rotator is connected");
                return;
            }
            progress?.Report(new ApplicationStatus { Status = $"Rotating the camera to {PositionAngle:0.#}°…" });
            try {
                if (info.Synced) {
                    // a plate solve has synced the rotator: turn to the sky angle
                    var reached = await rotator.Move((float)PositionAngle, token);
                    log.Info($"Camera rotated to {reached:0.#}° (target {PositionAngle:0.#}°)");
                } else {
                    // NINA knows the sky angle only after a plate solve; without one, turn to the same mechanical angle
                    var reached = await rotator.MoveMechanical((float)PositionAngle, token);
                    log.Info($"Camera rotated to the mechanical angle {reached:0.#}°: the rotator has not been synced by a plate solve, so the sky angle can differ. "
                        + "\"Slew and center\" rotates to the exact sky angle.");
                }
            } catch (Exception ex) when (ex is not OperationCanceledException) {
                log.Info($"Rotation to {PositionAngle:0.#}° failed: {ex.Message}. Imaging goes on at the current angle.");
                throw;
            }
        }

        public override TimeSpan GetEstimatedDuration() => TimeSpan.FromSeconds(30);

        public override string ToString() => $"Category: {Category}, Item: {nameof(PlannerRotate)}, PA: {PositionAngle}";
    }

    /// <summary>What the flat panel must do for the next frames.</summary>
    public enum PanelUse {
        /// <summary>Light frames, plate solving, autofocus: cover open, light off.</summary>
        Sky,
        /// <summary>Flat frames: cover closed, light on (at the panel's brightness).</summary>
        Flat,
        /// <summary>Darks and bias: cover closed, light off.</summary>
        Dark,
    }

    /// <summary>
    /// Sets a connected flat panel (cover and light) for the frames that follow, so light frames are never taken through a
    /// closed cover (e.g. one 4 End closed) and flat rows are lit. Does nothing without a flat panel; each part only when the
    /// panel has it (a cover, a switchable light).
    /// </summary>
    public static class FlatPanel {

        /// <summary>The panel use for a row's frames.</summary>
        public static PanelUse For(ExposureType type) => type switch {
            ExposureType.Flat => PanelUse.Flat,
            ExposureType.Dark or ExposureType.Bias or ExposureType.DarkFlat => PanelUse.Dark,
            _ => PanelUse.Sky
        };

        /// <summary>The steps needed: (close the cover?, open the cover?, light on?, light off?).</summary>
        public static (bool Close, bool Open, bool LightOn, bool LightOff) Steps(PanelUse use, bool hasCover, CoverState cover, bool hasLight, bool lightOn) {
            var close = hasCover && use != PanelUse.Sky && cover != CoverState.Closed;
            var open = hasCover && use == PanelUse.Sky && cover != CoverState.Open;
            var on = hasLight && use == PanelUse.Flat && !lightOn;
            var off = hasLight && use != PanelUse.Flat && lightOn;
            return (close, open, on, off);
        }

        /// <summary>Sets the panel for <paramref name="use"/>. True when the light is on afterwards.</summary>
        public static async Task<bool> Prepare(IFlatDeviceMediator panel, PanelUse use, IPlannerLog log, CancellationToken token) {
            var info = panel?.GetInfo();
            if (info?.Connected != true) { return false; }
            var hasCover = info.SupportsOpenClose && info.CoverState != CoverState.NotPresent;
            var (close, open, on, off) = Steps(use, hasCover, info.CoverState, info.SupportsOnOff, info.LightOn);
            // the light goes off before the cover opens, and the cover closes before the light goes on
            if (off) { await panel.ToggleLight(false, null, token); log.Info("Flat panel: light off"); }
            if (close) { await panel.CloseCover(null, token); log.Info("Flat panel: cover closed"); }
            if (open) { await panel.OpenCover(null, token); log.Info("Flat panel: cover opened"); }
            if (on) { await panel.ToggleLight(true, null, token); log.Info("Flat panel: light on for the flat frames"); }
            return panel.GetInfo()?.LightOn == true;
        }
    }

    /// <summary>Opens a closed flat panel cover (light off) before a target's slew, centering and frames.</summary>
    internal class PlannerOpenPanel : SequenceItem {
        private readonly IFlatDeviceMediator panel;
        private readonly IPlannerLog log;

        public PlannerOpenPanel(IFlatDeviceMediator panel, IPlannerLog log) {
            this.panel = panel;
            this.log = log;
            Name = "Open the flat panel cover";
            Category = "Flat Device";
        }

        public override object Clone() => new PlannerOpenPanel(panel, log);

        public override Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) => FlatPanel.Prepare(panel, PanelUse.Sky, log, token);

        public override TimeSpan GetEstimatedDuration() => TimeSpan.FromSeconds(20);

        public override string ToString() => $"Category: {Category}, Item: {nameof(PlannerOpenPanel)}";
    }
}
