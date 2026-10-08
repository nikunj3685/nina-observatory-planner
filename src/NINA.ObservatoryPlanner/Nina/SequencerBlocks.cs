using Newtonsoft.Json;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.ObservatoryPlanner.Core;
using NINA.Sequencer.Container;
using NINA.Sequencer.Container.ExecutionStrategy;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.SequenceItem.Imaging;
using NINA.Sequencer.Trigger;
using NINA.Sequencer.Trigger.Autofocus;
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
    /// Autofocus when the filter changes between light frames, for the planner's imaging. Unlike NINA's "AF After Filter
    /// Change" it compares with the filter of the previous light frame, also across targets, pauses and weather stops,
    /// and not with the filter of the last autofocus. Dark, bias and flat frames are ignored.
    /// </summary>
    [ExportMetadata("Name", PlannerAutofocusOnFilterChange.DisplayName)]
    [ExportMetadata("Description", "Observatory Planner: autofocus before a light frame whose filter differs from the previous light frame, also after a new target, a pause or a weather stop.")]
    [ExportMetadata("Icon", "AutoFocusAfterFilterSVG")]
    [ExportMetadata("Category", "Observatory Planner")]
    [Export(typeof(ISequenceTrigger))]
    [JsonObject(MemberSerialization.OptIn)]
    public class PlannerAutofocusOnFilterChange : SequenceTrigger {
        public const string DisplayName = "AF after filter change (planner)";
        private readonly PlannerService planner;

        [ImportingConstructor]
        public PlannerAutofocusOnFilterChange(PlannerService planner) : base() {
            this.planner = planner;
            try {
                var af = planner?.Factory?.RunAutofocus();
                if (af != null) { TriggerRunner.Add(af); }
            } catch (Exception ex) { Logger.Error("Observatory Planner: could not create the autofocus of AF after filter change (planner)", ex); }
        }

        private PlannerAutofocusOnFilterChange(PlannerAutofocusOnFilterChange cloneMe) : this(cloneMe.planner) {
            CopyMetaData(cloneMe);
        }

        public override object Clone() => new PlannerAutofocusOnFilterChange(this);

        public override bool ShouldTrigger(ISequenceItem previousItem, ISequenceItem nextItem) =>
            nextItem is TakeExposure && planner?.FilterChangeNeedsFocus() == true;

        public override async Task Execute(ISequenceContainer context, IProgress<ApplicationStatus> progress, CancellationToken token) {
            planner?.Log.Info($"Filter changed from {planner.LastLightFilter}: autofocus before the next frame");
            try {
                await TriggerRunner.Run(progress, token);
            } finally {
                planner?.MarkFocused();
            }
        }

        public override string ToString() => $"Category: {Category}, Item: {nameof(PlannerAutofocusOnFilterChange)}";
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
            RememberLastFilter();
            planner.ImagingTargetId = target.Id; // 2 Start of target ran to its end
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

                    // autofocus on the frame's filter: the panel's Autofocus button, or the first light frame after 1 Begin
                    planner.PrepareFrame(e);
                    var requested = planner.TakeAutofocusRequest();
                    var afterBegin = planner.TakeFocusAfterBegin(e);
                    if (requested || afterBegin) {
                        var why = requested ? "requested autofocus" : "autofocus (first frame after 1 Begin)";
                        var af = planner.Factory.RunAutofocus();
                        await Ui.Run(() => { foreach (var old in Items.ToList()) { Remove(old); } if (sw != null) { Add(sw); } Add(af); Add(take); });
                        planner.Log.Info($"{target.Name}: running the {why} before the next frame");
                        var ok = await RunItem(af, progress, token);
                        planner.MarkFocused();
                        planner.Log.Info($"{target.Name}: {why} {(ok ? "finished" : "failed; imaging goes on with the focus as it is")}");
                    }
                    await RunTriggers(previous, take, progress, token);

                    var light = e.Type == ExposureType.Light;
                    var star = planner.Options.GuideLostWatch && light ? new GuiderWatch(planner.GuideStarLost, () => DateTime.Now) : null;
                    var error = planner.Options.GuidingCheck && light ? new GuiderWatch(planner.GuidingErrorAbove, () => DateTime.Now) : null;
                    if (star != null && planner.GuideStarLost() && !await WaitForGuideStar(star, token)) { break; }
                    // after the wait ran out the frame starts anyway and is not restarted for the guiding error (no endless restarts)
                    if (error != null && planner.GuidingErrorAbove() && !await WaitForGuiding(error, token)) { error = null; }

                    // a pause asked for during the triggers (e.g. a dither) takes effect before the frame, not by aborting it
                    if (!planner.FrameAllowed) {
                        planner.Log.Info($"{target.Name}: pausing before the next frame");
                        break;
                    }
                    planner.OnFrameStarting(target, e);
                    var (taken, dropped) = await TakeFrame(take, star, error, progress, token);
                    if (dropped == FrameDrop.StarLost) {
                        planner.Log.Info($"{target.Name}: the guide star was lost during the frame; the frame is dropped and taken again");
                        previous = take;
                        if (!await WaitForGuideStar(star, token)) { break; }
                        continue;
                    }
                    if (dropped == FrameDrop.GuidingError) {
                        planner.Log.Info($"{target.Name}: the guiding error stayed above {planner.Options.GuidingLimitPixels:0.##} px for 10 s; the frame is dropped and taken again");
                        previous = take;
                        continue;
                    }
                    if (taken) {
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
                planner.OnImagingEnded(target);
                foreach (var t in Triggers.ToList()) {
                    try { t.SequenceBlockFinished(); t.SequenceBlockTeardown(); } catch (Exception ex) { Logger.Error(ex); }
                }
            }
        }

        /// <summary>
        /// NINA's "AF After Filter Change" starts each time from the filter the wheel is on. The planner creates it anew for
        /// every target and after every pause or weather stop, and 2 Start of target can move the wheel (plate solving), so
        /// the first filter change would be missed. It starts from the filter of the last light frame instead.
        /// </summary>
        private void RememberLastFilter() {
            var last = planner.LastLightFilter;
            if (last == null) { return; }
            foreach (var af in Triggers.OfType<AutofocusAfterFilterChange>()) { SeedLastFilter(af, last); }
        }

        /// <summary>Sets the filter "AF After Filter Change" compares with (NINA keeps its setter private). False when that fails.</summary>
        internal static bool SeedLastFilter(AutofocusAfterFilterChange af, string filter) {
            try {
                var setter = typeof(AutofocusAfterFilterChange).GetProperty(nameof(AutofocusAfterFilterChange.LastAutoFocusFilter))?.GetSetMethod(nonPublic: true);
                if (setter == null) { Logger.Warning("Observatory Planner: AF After Filter Change has no LastAutoFocusFilter setter"); return false; }
                setter.Invoke(af, new object[] { filter });
                return true;
            } catch (Exception ex) {
                Logger.Error(ex);
                return false;
            }
        }

        private enum FrameDrop { None, StarLost, GuidingError }

        /// <summary>
        /// Takes the frame. With guider watches, the guider is checked every second and the frame is aborted once the star
        /// has been lost, or the guiding error has been above the limit, for 10 s. Returns whether the frame was taken and
        /// why it was dropped.
        /// </summary>
        private async Task<(bool Taken, FrameDrop Dropped)> TakeFrame(TakeExposure take, GuiderWatch star, GuiderWatch error, IProgress<ApplicationStatus> progress, CancellationToken token) {
            if (star == null && error == null) { return (await RunItem(take, progress, token) && take.Status == SequenceEntityStatus.FINISHED, FrameDrop.None); }
            using var frame = CancellationTokenSource.CreateLinkedTokenSource(token);
            var dropped = FrameDrop.None;
            var watcher = Task.Run(async () => {
                try {
                    while (!frame.IsCancellationRequested) {
                        await Task.Delay(TimeSpan.FromSeconds(1), frame.Token);
                        if (star?.Poll() == true) { dropped = FrameDrop.StarLost; frame.Cancel(); return; }
                        if (error?.Poll() == true) { dropped = FrameDrop.GuidingError; frame.Cancel(); return; }
                    }
                } catch (OperationCanceledException) { }
            });
            try {
                await take.Run(progress, frame.Token);
                return (take.Status == SequenceEntityStatus.FINISHED, FrameDrop.None);
            } catch (OperationCanceledException) when (dropped != FrameDrop.None && !token.IsCancellationRequested) {
                return (false, dropped);
            } catch (OperationCanceledException) when (token.IsCancellationRequested) {
                throw;
            } catch (Exception ex) {
                Logger.Error($"Observatory Planner: {take.Name} failed", ex);
                planner.Log.Info($"{take.Name} failed: {ex.Message}");
                return (false, FrameDrop.None);
            } finally {
                if (!frame.IsCancellationRequested) { frame.Cancel(); }
                try { await watcher; } catch (OperationCanceledException) { }
            }
        }

        /// <summary>Waits for the guider to find the star again. False when it did not within the wait (the planner then acts).</summary>
        private async Task<bool> WaitForGuideStar(GuiderWatch watch, CancellationToken token) {
            var wait = TimeSpan.FromSeconds(planner.Options.GuideLostWaitSeconds);
            planner.Log.Info($"{target.Name}: the guide star is lost; waiting up to {wait.TotalSeconds:0} s for the guider to find it again");
            var found = await watch.WaitUntilGood(wait, (d, t) => Task.Delay(d, t),
                left => planner.SetStatus($"Guide star lost on {target.Name}: waiting for the guider to find it again ({PlannerEngine.Countdown(left)} left)"), token);
            if (found) {
                planner.Log.Info($"{target.Name}: the guide star was found again; imaging goes on");
                return true;
            }
            planner.Log.Info($"{target.Name}: the guide star was not found again within {wait.TotalSeconds:0} s");
            planner.GaveUpOnGuideStar();
            return false;
        }

        /// <summary>Waits for the guiding error to come below the limit. False when the wait ran out (the frame then starts anyway).</summary>
        private async Task<bool> WaitForGuiding(GuiderWatch watch, CancellationToken token) {
            var wait = TimeSpan.FromSeconds(planner.Options.GuidingCheckWaitSeconds);
            var limit = planner.Options.GuidingLimitPixels;
            planner.Log.Info($"{target.Name}: guiding error {planner.GuidingError():0.##} px is above {limit:0.##} px; waiting up to {wait.TotalSeconds:0} s before the next frame");
            var good = await watch.WaitUntilGood(wait, (d, t) => Task.Delay(d, t),
                left => planner.SetStatus($"Guiding error {planner.GuidingError():0.##} px (limit {limit:0.##} px): waiting before the next frame ({PlannerEngine.Countdown(left)} left)"), token);
            planner.Log.Info(good
                ? $"{target.Name}: guiding error {planner.GuidingError():0.##} px is below the limit; the frame starts"
                : $"{target.Name}: the guiding error stayed above {limit:0.##} px for {wait.TotalSeconds:0} s; the frame starts anyway");
            return good;
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
