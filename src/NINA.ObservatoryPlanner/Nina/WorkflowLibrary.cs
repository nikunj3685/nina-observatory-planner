using NINA.Core.Locale;
using NINA.ObservatoryPlanner.Core;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Trigger;
using System;
using System.Collections.Generic;
using System.Linq;

namespace NINA.ObservatoryPlanner.Nina {

    /// <summary>
    /// The built-in workflows. Every stage holds ordinary NINA instructions the user can edit in the Advanced Sequencer.
    /// Power switches are named after the observatory's switch hub and looked up by name when the workflow is loaded.
    /// </summary>
    public static class WorkflowLibrary {
        public const string SafetyAndDome = "Safety and dome";
        public const string NoSafetyWithDome = "No safety with dome";
        public const string NoSafetyNoDome = "No safety, no dome";

        public static readonly string[] Names = { SafetyAndDome, NoSafetyWithDome, NoSafetyNoDome };

        public static readonly Dictionary<string, string> Notes = new() {
            [SafetyAndDome] = "Unattended observatory with a dome or roll-off roof and a safety monitor. Runs every night.",
            [NoSafetyWithDome] = "Dome or roll-off roof without a safety monitor. Starts at astronomical dusk, runs once. Stay nearby for weather.",
            [NoSafetyNoDome] = "Mount outdoors, no dome or roof and no safety monitor. Starts at astronomical dusk, runs once. Stay nearby."
        };

        /// <summary>Switches turned on in 1 Begin, in this order.</summary>
        public static readonly string[] PowerOn = { "ZWO 2600 MM P", "ZWO 2600 MM", "ZWO EAF P", "ZWO EAF", "SW CQ-350", "ZWO 290 MM", "Filter Wheel" };

        /// <summary>Position on the hub used when a switch name is not found (the order of <see cref="PowerOn"/>).</summary>
        private static short Fallback(string name) => (short)Array.IndexOf(PowerOn, name);

        public static RunMode ModeOf(string name) => name == SafetyAndDome ? RunMode.WithSafety : RunMode.WithoutSafety;

        public static string DefaultFor(RunMode mode) => mode == RunMode.WithSafety ? SafetyAndDome : NoSafetyWithDome;

        public static ObservatoryPlannerContainer Build(string name, PlannerService planner) {
            var f = planner.Factory;
            var dome = name != NoSafetyNoDome;
            var safety = name == SafetyAndDome;

            ISequenceItem On(string sw) => f.SetSwitch(sw, 1, Fallback(sw));
            ISequenceItem Off(string sw) => f.SetSwitch(sw, 0, Fallback(sw));

            var begin = new List<ISequenceItem>();
            if (!safety) { begin.Add(f.WaitForTime("DuskProvider")); }
            begin.AddRange(PowerOn.Select(On));
            begin.Add(f.WaitSeconds(15));
            begin.Add(f.ConnectAll());
            begin.Add(f.CoolCamera(-20, 5));
            if (dome) { begin.Add(f.OpenDome()); }
            if (dome && !safety) { begin.Add(f.EnableDomeSync()); }
            begin.Add(f.WaitSeconds(60));
            // 4 End parks the mount and NINA does not slew a parked mount, so 1 Begin unparks it.
            begin.Add(f.Unpark());

            var targetStart = new List<ISequenceItem> { f.Center(), f.StartGuiding(true) };

            var triggers = new List<ISequenceTrigger> { f.DitherAfter(1), f.AutofocusAfterFilterChange(), f.MeridianFlip() };
            if (dome && !safety) { triggers.Add(f.SynchronizeDome()); }

            // Connect Mount first: if the weather turned unsafe early in 1 Begin, the mount is not connected yet and could not park.
            var end = new List<ISequenceItem> { f.StopGuiding(), f.Connect("Mount"), f.Park() };
            if (dome) { end.Add(f.CloseDome()); }
            if (dome && !safety) { end.Add(f.DisableDomeSync()); }
            end.Add(f.WaitSeconds(60));
            end.Add(f.WarmCamera(5));
            end.Add(f.Disconnect("Camera"));
            end.Add(Off("ZWO 2600 MM"));
            end.Add(Off("ZWO 2600 MM P"));
            end.Add(f.Disconnect("Filter Wheel"));
            end.Add(Off("Filter Wheel"));
            end.Add(f.Disconnect("Focuser"));
            end.Add(Off("ZWO EAF"));
            end.Add(Off("ZWO EAF P"));
            end.Add(f.Disconnect("Mount"));
            end.Add(Off("SW CQ-350"));
            end.Add(f.Disconnect("Guider"));
            end.Add(Off("ZWO 290 MM"));

            return Assemble(planner, begin, targetStart, triggers, end);
        }

        public static ObservatoryPlannerContainer Assemble(PlannerService planner, IEnumerable<ISequenceItem> begin, IEnumerable<ISequenceItem> targetStart,
                                                           IEnumerable<ISequenceTrigger> triggers, IEnumerable<ISequenceItem> end) {
            var container = new ObservatoryPlannerContainer(planner) {
                Name = "Observatory Planner",
                Category = "Observatory Planner",
                Icon = System.Windows.Application.Current?.TryFindResource("TelescopeSVG") as System.Windows.Media.GeometryGroup
            };
            container.Add(Stage(StageKind.Begin, begin, null));
            container.Add(Stage(StageKind.TargetStart, targetStart, null));
            container.Add(Stage(StageKind.Triggers, Array.Empty<ISequenceItem>(), triggers));
            container.Add(Stage(StageKind.End, end, null));
            return container;
        }

        private static PlannerStageContainer Stage(StageKind kind, IEnumerable<ISequenceItem> items, IEnumerable<ISequenceTrigger> triggers) {
            var stage = new PlannerStageContainer {
                Stage = kind,
                Name = PlannerStageContainer.Title(kind),
                Category = "Observatory Planner",
                Icon = System.Windows.Application.Current?.TryFindResource("SequentialSVG") as System.Windows.Media.GeometryGroup
            };
            foreach (var i in items) { stage.Add(i); }
            foreach (var t in triggers ?? Enumerable.Empty<ISequenceTrigger>()) { stage.Add(t); }
            return stage;
        }

        /// <summary>A new Advanced Sequencer sequence (Start / Targets / End areas) with the planner block in the target area.</summary>
        public static SequenceRootContainer NewSequence(ObservatoryPlannerContainer planner) {
            string L(string key) { try { var v = Loc.Instance[key]; return string.IsNullOrEmpty(v) ? key : v; } catch (Exception) { return key; } }
            var root = new SequenceRootContainer { Name = L("Lbl_SequenceContainer_SequenceRootContainer_Name"), SequenceTitle = "Observatory Planner" };
            var start = new StartAreaContainer { Name = L("Lbl_SequenceContainer_StartAreaContainer_Name") };
            var targets = new TargetAreaContainer { Name = L("Lbl_SequenceContainer_TargetAreaContainer_Name") };
            var endArea = new EndAreaContainer { Name = L("Lbl_SequenceContainer_EndAreaContainer_Name") };
            root.Add(start);
            root.Add(targets);
            root.Add(endArea);
            targets.Add(planner);
            return root;
        }

        public static IReadOnlyList<StageEntry> Describe(PlannerStageContainer stage) {
            if (stage == null) { return Array.Empty<StageEntry>(); }
            var targetStage = stage.Stage == StageKind.TargetStart;
            var entries = stage.Items.Select(i => new StageEntry(i.GetType().Name, i.Name, InstructionFactory.DeviceOf(i),
                    InstructionSummary.CategoryOf(i), InstructionSummary.Of(i, targetStage)))
                .Concat(stage.Triggers.Select(t => new StageEntry(t.GetType().Name, t.Name, null,
                    InstructionSummary.CategoryOf(t), InstructionSummary.Of(t, targetStage))));
            return entries.ToList();
        }
    }
}
