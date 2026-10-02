using Newtonsoft.Json;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.ObservatoryPlanner.Core;
using NINA.Sequencer.Container;
using NINA.Sequencer.Container.ExecutionStrategy;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Trigger;
using NINA.Sequencer.Utility;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.ObservatoryPlanner.Nina {

    /// <summary>
    /// One of the four stages. A plain sequential instruction set the user edits in the Advanced Sequencer;
    /// the type only tells the planner which stage it is.
    /// </summary>
    [ExportMetadata("Name", "Planner stage")]
    [ExportMetadata("Description", "One stage of the Observatory Planner (Begin, Start of target, Triggers or End).")]
    [ExportMetadata("Icon", "SequentialSVG")]
    [ExportMetadata("Category", "Observatory Planner")]
    [Export(typeof(ISequenceContainer))]
    [JsonObject(MemberSerialization.OptIn)]
    public class PlannerStageContainer : SequentialContainer {

        [ImportingConstructor]
        public PlannerStageContainer() : base() { }

        [JsonProperty]
        public StageKind Stage { get; set; } = StageKind.Begin;

        public static string Title(StageKind stage) => stage switch {
            StageKind.Begin => "1 Begin",
            StageKind.TargetStart => "2 Start of each target",
            StageKind.Triggers => "3 While imaging: triggers",
            _ => "4 End"
        };

        public override object Clone() {
            var clone = new PlannerStageContainer {
                Stage = Stage, Icon = Icon, Name = Name, Category = Category, Description = Description,
                Items = new ObservableCollection<ISequenceItem>(Items.Select(i => i.Clone() as ISequenceItem)),
                Triggers = new ObservableCollection<ISequenceTrigger>(Triggers.Select(t => t.Clone() as ISequenceTrigger)),
            };
            foreach (var item in clone.Items) { item.AttachNewParent(clone); }
            foreach (var trigger in clone.Triggers) { trigger.AttachNewParent(clone); }
            return clone;
        }
    }

    /// <summary>
    /// The Observatory Planner block. Holds the four stages; when it runs it hands control to the night engine,
    /// which runs the stages and builds one target block at a time from the planner's target list.
    /// </summary>
    [ExportMetadata("Name", "Observatory Planner")]
    [ExportMetadata("Description", "Runs the Observatory Planner: 1 Begin, your targets with 2 Start of target and 3 triggers, then 4 End. With a safety monitor it repeats every night.")]
    [ExportMetadata("Icon", "TelescopeSVG")]
    [ExportMetadata("Category", "Observatory Planner")]
    [Export(typeof(ISequenceContainer))]
    [JsonObject(MemberSerialization.OptIn)]
    public class ObservatoryPlannerContainer : SequenceContainer {
        private readonly PlannerService planner;

        [ImportingConstructor]
        public ObservatoryPlannerContainer(PlannerService planner) : base(new SequentialStrategy()) {
            this.planner = planner;
        }

        public PlannerStageContainer Stage(StageKind kind) => Items.OfType<PlannerStageContainer>().FirstOrDefault(s => s.Stage == kind);

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            // Leftover target blocks from a sequence saved while running are not part of the plan.
            await Ui.Run(() => { foreach (var stray in Items.Where(i => i is not PlannerStageContainer).ToList()) { Remove(stray); } });
            planner.Register(this);
            await planner.RunEngineAsync(this, progress, token);
        }

        public override void AfterParentChanged() {
            base.AfterParentChanged();
            if (ItemUtility.GetRootContainer(Parent) != null) { planner.Register(this); }
        }

        public override object Clone() {
            var clone = new ObservatoryPlannerContainer(planner) {
                Icon = Icon, Name = Name, Category = Category, Description = Description,
                Items = new ObservableCollection<ISequenceItem>(Items.Select(i => i.Clone() as ISequenceItem)),
            };
            foreach (var item in clone.Items) { item.AttachNewParent(clone); }
            return clone;
        }

        public override string ToString() => $"Category: {Category}, Item: {nameof(ObservatoryPlannerContainer)}";
    }

    /// <summary>
    /// The exposures of one target, created while it runs. Picks the next row (finish each row / rotate),
    /// switches filter, takes the frame with NINA's Take Exposure and runs the stage 3 triggers between frames.
    /// </summary>
    public class PlannerImagingRun : SequenceContainer {
        private readonly PlannerService planner;
        private readonly PlannerTarget target;
        private readonly Func<PlannerExposure, bool> canStartFrame;

        public PlannerImagingRun(PlannerService planner, PlannerTarget target, Func<PlannerExposure, bool> canStartFrame) : base(new SequentialStrategy()) {
            this.planner = planner;
            this.target = target;
            this.canStartFrame = canStartFrame;
            Name = $"Imaging {target.Name}";
        }

        public int FramesTaken { get; private set; }

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            FramesTaken = 0;
            PlannerExposure last = null;
            ISequenceItem previous = null;
            var failures = 0;
            foreach (var t in Triggers.ToList()) { t.SequenceBlockInitialize(); t.SequenceBlockStarted(); }
            try {
                while (true) {
                    token.ThrowIfCancellationRequested();
                    var e = ExposurePlanner.Next(target, last);
                    if (e == null) { break; }
                    if (!canStartFrame(e)) {
                        planner.Log.Info(planner.FrameAllowed
                            ? $"{target.Name}: the window closes before a {e.ExposureTime:0.#} s frame would finish"
                            : $"{target.Name}: pausing before the next frame");
                        break;
                    }

                    if (planner.TakeAutofocusRequest()) {
                        var af = planner.Factory.RunAutofocus();
                        await Ui.Run(() => { foreach (var old in Items.ToList()) { Remove(old); } Add(af); });
                        planner.Log.Info($"{target.Name}: running the requested autofocus before the next frame");
                        var ok = await RunItem(af, progress, token);
                        planner.Log.Info($"{target.Name}: requested autofocus {(ok ? "finished" : "failed")}");
                    }

                    var sw = planner.Factory.SwitchFilter(e.Filter);
                    var take = planner.Factory.TakeExposure(e);
                    await Ui.Run(() => {
                        foreach (var old in Items.ToList()) { Remove(old); }
                        if (sw != null) { Add(sw); }
                        Add(take);
                    });

                    if (sw != null) {
                        await RunTriggers(previous, sw, progress, token);
                        if (!await RunItem(sw, progress, token)) { failures++; }
                        previous = sw;
                    }
                    await RunTriggers(previous, take, progress, token);
                    planner.OnFrameStarting(target, e);
                    if (await RunItem(take, progress, token) && take.Status == SequenceEntityStatus.FINISHED) {
                        e.Done++;
                        FramesTaken++;
                        failures = 0;
                        planner.OnFrameTaken(target, e);
                    } else {
                        failures++;
                    }
                    last = e;
                    previous = take;
                    if (failures >= 3) {
                        planner.Log.Info($"{target.Name}: 3 failed frames in a row, moving on");
                        break;
                    }
                    var next = ExposurePlanner.Next(target, last);
                    await RunTriggersAfter(previous, null, progress, token);
                    if (next != null && target.DelayBetween > 0) {
                        await Task.Delay(TimeSpan.FromSeconds(target.DelayBetween), token);
                    }
                }
            } finally {
                foreach (var t in Triggers.ToList()) {
                    try { t.SequenceBlockFinished(); t.SequenceBlockTeardown(); } catch (Exception ex) { Logger.Error(ex); }
                }
            }
        }

        /// <summary>Runs one instruction. A failed instruction must not end the night; a cancellation must.</summary>
        private async Task<bool> RunItem(ISequenceItem item, IProgress<ApplicationStatus> progress, CancellationToken token) {
            try {
                await item.Run(progress, token);
                return item.Status != SequenceEntityStatus.FAILED;
            } catch (OperationCanceledException) when (token.IsCancellationRequested) {
                throw;
            } catch (Exception ex) {
                Logger.Error($"Observatory Planner: {item.Name} failed", ex);
                planner.Log.Info($"{item.Name} failed: {ex.Message}");
                return false;
            }
        }

        public override object Clone() => new PlannerImagingRun(planner, target, canStartFrame);
    }
}
