using NINA.Core.Enum;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.ObservatoryPlanner.Core;
using NINA.ObservatoryPlanner.Nina;
using NINA.ObservatoryPlanner.UI.Dialogs;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.ViewModel;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace NINA.ObservatoryPlanner.UI {

    /// <summary>One instruction in a stage box: number, category, name and a summary of its settings.</summary>
    public sealed record StageLine(int Number, string Category, string Name, string Summary) {
        public string SummaryText => string.IsNullOrEmpty(Summary) ? "" : "  · " + Summary;
    }

    public class StageView : Observable {
        private IReadOnlyList<StageLine> lines = Array.Empty<StageLine>();
        private IReadOnlyList<string> warnings = Array.Empty<string>();
        private string title;
        private string hint;
        private string emptyText;
        public int Number { get; init; }
        public Brush Brush { get; init; }
        public string Title { get => title; set => Set(ref title, value); }
        public string Hint { get => hint; set => Set(ref hint, value); }
        public string EmptyText { get => emptyText; set => Set(ref emptyText, value); }
        public IReadOnlyList<StageLine> Lines { get => lines; set { if (!lines.SequenceEqual(value)) { lines = value; Raise(); } } }
        public IReadOnlyList<string> Warnings { get => warnings; set { if (!warnings.SequenceEqual(value)) { warnings = value; Raise(); } } }
    }

    public sealed record StepChip(string Text, bool HasNext);

    public class KeepItem : Observable {
        private readonly PlannerOptions options;
        public KeepItem(string device, PlannerOptions options) {
            Device = device;
            this.options = options;
        }
        public string Device { get; }
        public string Label => Device == "Switch" ? "Switch hub" : Device;
        public bool Locked => Device == "Safety Monitor" && options.RunMode == RunMode.WithSafety;
        public string Note => Locked ? "required with safety" : null;
        public bool IsChecked {
            get => options.Keeps(Device);
            set {
                if (Locked) { return; }
                if (value && !options.KeepConnected.Contains(Device)) { options.KeepConnected.Add(Device); }
                if (!value) { options.KeepConnected.Remove(Device); }
                Raise();
            }
        }
        public void Refresh() { Raise(nameof(IsChecked)); Raise(nameof(Locked)); Raise(nameof(Note)); }
    }

    /// <summary>The Observatory Planner panel (Imaging tab, can float in its own window).</summary>
    [Export(typeof(IDockableVM))]
    public class PlannerDockableVM : DockableVM {
        private readonly PlannerService planner;
        private readonly WorkflowFiles workflows;
        private PlannerTarget selectedTarget;
        private string rotateAfWarning;
        private bool? safe;
        private readonly DispatcherTimer refreshTimer;
        private readonly DispatcherTimer saveTimer;

        [ImportingConstructor]
        public PlannerDockableVM(IProfileService profileService, PlannerService planner) : base(profileService) {
            this.planner = planner;
            workflows = planner.Workflows;
            Title = "Observatory Planner";
            if (Application.Current?.TryFindResource("TelescopeSVG") is GeometryGroup icon) { ImageGeometry = icon; }

            GapMountChoices = new[] { new Choice(GapMountAction.KeepTracking, "Keep tracking"), new Choice(GapMountAction.StopTrackingAndPark, "Stop tracking and Park"), new Choice(GapMountAction.StopTrackingAndFindHome, "Stop tracking and Find home") };
            BinningChoices = new[] { "1x1", "2x2", "3x3", "4x4" };
            ExposureTypeChoices = new[] {
                new Choice(ExposureType.Light, "Light"), new Choice(ExposureType.Dark, "Dark"), new Choice(ExposureType.Bias, "Bias"),
                new Choice(ExposureType.Flat, "Flat")
            };
            KeepItems = NinaPlannerHardware.Devices.Select(d => new KeepItem(d, Options)).ToList();
            Brush[] stageBrushes = { PlannerBrushes.Safe, PlannerBrushes.Info, PlannerBrushes.Accent, PlannerBrushes.Unsafe };
            Stages = new ObservableCollection<StageView>(Enumerable.Range(1, 4).Select(n => new StageView { Number = n, Brush = stageBrushes[n - 1] }));

            RunCommand = new Command(Run);
            AddEmptyCommand = new Command(AddEmpty);
            AddFromFramingCommand = new Command(() => AddFromFraming(mosaic: false));
            AddMosaicCommand = new Command(() => AddFromFraming(mosaic: true));
            DuplicateCommand = new Command(Duplicate, _ => SelectedTarget != null);
            MoveUpCommand = new Command(() => MoveTarget(-1), _ => SelectedTarget != null && Targets.IndexOf(SelectedTarget) > 0);
            MoveDownCommand = new Command(() => MoveTarget(1), _ => SelectedTarget != null && Targets.IndexOf(SelectedTarget) < Targets.Count - 1);
            DeleteCommand = new Command(DeleteTarget, _ => SelectedTarget != null);
            TargetSettingsCommand = new Command(p => OpenTargetSettings(p as PlannerTarget ?? SelectedTarget, isNew: false), _ => SelectedTarget != null || _ is PlannerTarget);
            AddExposureCommand = new Command(AddExposure, _ => SelectedTarget != null);
            DeleteExposureCommand = new Command(DeleteExposure);
            ExposureUpCommand = new Command(p => MoveExposure(p, -1));
            ExposureDownCommand = new Command(p => MoveExposure(p, 1));
            ResetProgressCommand = new Command(ResetProgress, _ => SelectedTarget != null);
            NewListCommand = new Command(NewList);
            OpenListCommand = new Command(OpenList);
            SaveListCommand = new Command(SaveList);
            SaveListAsCommand = new Command(SaveListAs);
            OpenWorkflowCommand = new Command(OpenWorkflow);
            SaveWorkflowCommand = new Command(() => SaveWorkflow(askName: false), _ => planner.ActiveContainer != null);
            SaveWorkflowAsCommand = new Command(() => SaveWorkflow(askName: true), _ => planner.ActiveContainer != null);
            LoadDefaultWorkflowCommand = new Command(ChooseDefaultWorkflow);
            EditInSequencerCommand = new Command(EditInSequencer);
            RefreshStagesCommand = new Command(RefreshStages);
            AutofocusCommand = new Command(() => planner.RequestAutofocus(), _ => planner.IsRunning && planner.Phase == PlannerPhase.Imaging);
            PauseNowCommand = new Command(() => planner.RequestPause(PauseKind.Now), _ => PauseEnabled);
            PauseAfterFrameCommand = new Command(() => planner.RequestPause(PauseKind.AfterFrame), _ => PauseEnabled);
            CancelPauseCommand = new Command(() => planner.CancelPause(), _ => planner.PausePending);
            ResumeCommand = new Command(() => planner.Resume(), _ => planner.IsPaused && !planner.IsRunning);
            GoOptionsCommand = new Command(() => SelectedPlannerTab = 2);
            GoInfoCommand = new Command(() => SelectedPlannerTab = 3);
            ApplyDefaultsToAllCommand = new Command(ApplyDefaultsToAll);
            ResetConfirmationsCommand = new Command(() => { Options.ConfirmDeleteTarget = true; Options.ConfirmDeleteExposure = true; });

            planner.PropertyChanged += (_, e) => Ui.Run(() => ServiceChanged(e.PropertyName));
            planner.ActiveContainerChanged += (_, _) => Ui.Run(() => { RefreshStages(); RaiseWorkflow(); });
            planner.ShowPlannerTabRequested = tab => { SelectedPlannerTab = tab; DockSelector.Select(this, Title); };
            planner.DialogRequested = name => Application.Current?.Dispatcher.BeginInvoke(new Action(() => OpenDialog(name)));
            planner.ShowPanelRequested = () => {
                IsVisible = true;
                try { planner.Nina.Application.ChangeTab(ApplicationTab.IMAGING); } catch (Exception ex) { Logger.Error(ex); }
                // the dock creates its tabs once the Imaging tab is shown; select ours after that
                Application.Current?.Dispatcher.BeginInvoke(new Action(() => DockSelector.Select(this, Title)), DispatcherPriority.ContextIdle);
            };
            Options.PropertyChanged += (_, e) => OptionChanged(e.PropertyName);
            Options.KeepConnected.CollectionChanged += (_, _) => { foreach (var k in KeepItems) { k.Refresh(); } RaisePropertyChanged(nameof(KeepChips)); RefreshStages(); };

            WatchTargets();
            SelectedTarget = Targets.FirstOrDefault();
            RefreshStages();

            // The stages are edited in NINA's own sequencer, and the safety monitor changes on its own: keep both current.
            // NINA creates plugin view models on a background thread: the timers must run on the UI thread's dispatcher,
            // or they never tick (a DispatcherTimer belongs to the thread that creates it).
            var ui = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
            refreshTimer = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromSeconds(3) };
            refreshTimer.Tick += (_, _) => {
                RefreshStages();
                RefreshSafety();
                RaisePropertyChanged(nameof(GuidingLimitArcsec));
                // save the workflow whenever it changes, so a NINA restart brings back the exact stages
                if (!planner.IsRunning) {
                    try { if (workflows.AutoSave()) { RaiseWorkflow(); } } catch (Exception ex) { Logger.Error(ex); }
                }
            };
            refreshTimer.Start();
            // Edits are saved automatically, a moment after the last change.
            saveTimer = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromSeconds(1) };
            saveTimer.Tick += (_, _) => { saveTimer.Stop(); try { planner.SaveTargets(); } catch (Exception ex) { Logger.Error(ex); } };
        }

        private int selectedPlannerTab;
        /// <summary>0 Targets, 1 Equipment &amp; Safety, 2 Options (opened with the gear), 3 Info (opened with the !).</summary>
        public int SelectedPlannerTab { get => selectedPlannerTab; set { selectedPlannerTab = value; RaisePropertyChanged(); if (value == 1) { RefreshStages(); } } }

        public PlannerOptions Options => planner.Options;
        public ObservableCollection<PlannerTarget> Targets => planner.Targets;

        // ---------- status strip and phase chain ----------
        public bool IsRunning => planner.IsRunning;
        public string RunButtonText => planner.IsRunning || planner.IsPaused ? "■ Stop" : (WithSafety ? "▶ Run forever" : "▶ Run");
        public string RunButtonTip => planner.IsRunning || planner.IsPaused
            ? "Stop: the frame being taken is dropped, 4 End runs (park, close, warm, power off) and the run ends."
            : "Start the night loop.";
        /// <summary>Running or paused: the main button is a Stop button (red outline).</summary>
        public bool StopShown => planner.IsRunning || planner.IsPaused;

        // the Pause button: Pause (menu) while running, Pausing… while a pause is due, Start sequence while paused
        public bool IsPaused => planner.IsPaused && !planner.IsRunning;
        public bool PauseShown => planner.IsRunning && !planner.PausePending;
        public bool PausingShown => planner.IsRunning && planner.PausePending;
        /// <summary>4 End always runs to the end (park, close, warm, power off), so it can't be paused.</summary>
        public bool PauseEnabled => planner.PauseAllowed && planner.Phase != PlannerPhase.End;
        public string PauseTip => planner.Phase == PlannerPhase.End
            ? "4 End is running: it always runs to the end (park, close, warm, power off), so it can't be paused."
            : !planner.PauseAllowed
                ? "Pause is available once 1 Begin has started. While waiting for safe or for the next night nothing is powered: use Stop, and Run when you're ready."
                : "Pause the run: everything stays powered and on target. Start sequence continues.";
        /// <summary>The guiding limit in arcseconds, from PHD2's pixel scale.</summary>
        public string GuidingLimitArcsec => planner.GuiderPixelScale is double scale
            ? $"≈ {Options.GuidingLimitPixels * scale:0.00}″ with PHD2's pixel scale of {scale:0.00}″/px"
            : "The limit in arcseconds is shown here while PHD2 is connected";

        /// <summary>4 End steps that failed the last time it ran, or null.</summary>
        public string EndProblems => planner.EndProblems;

        public string StatusText {
            get {
                if (planner.IsPaused && !planner.IsRunning) {
                    if (planner.PausePoint?.BeginDone is int done && planner.PauseNote == null) {
                        return $"Paused during 1 Begin, after step {done}. Press Start sequence to continue 1 Begin from step {done + 1}.";
                    }
                    var where = planner.PausePoint?.TargetName != null ? $"Paused on {planner.PausePoint.TargetName}." : "Paused.";
                    return planner.PauseNote != null ? $"{where} {planner.PauseNote}" : $"{where} Guiding stopped; tracking, dome and power stay on. Press Start sequence to continue.";
                }
                if (planner.IsRunning) { return planner.StatusText; }
                if (planner.Phase == PlannerPhase.Finished) { return WithSafety ? planner.StatusText : "All targets are done and 4 End has run. Press Run to start again."; }
                if (planner.Phase == PlannerPhase.Stopped && planner.StatusText.Contains("error")) { return planner.StatusText; }
                return WithSafety ? "Press Run forever to start the automatic night loop." : "Press Run to start: 1 Begin, then your targets, then 4 End.";
            }
        }

        public string PillText => planner.IsPaused && !planner.IsRunning ? "Paused"
            : !planner.IsRunning ? (planner.Phase == PlannerPhase.Finished ? "Finished" : "Stopped")
            : !WithSafety ? "No safety monitor"
            : safe == true ? "Safe" : safe == false ? "Unsafe" : "No safety data";

        public Brush PillBrush => planner.IsPaused && !planner.IsRunning ? PlannerBrushes.Accent
            : !planner.IsRunning || !WithSafety ? PlannerBrushes.Muted : safe == true ? PlannerBrushes.Safe : PlannerBrushes.Unsafe;

        private bool Step(params PlannerPhase[] phases) => planner.IsRunning && phases.Contains(planner.Phase);
        public bool StepWait => Step(PlannerPhase.WaitingForSafe, PlannerPhase.WaitingForNextNight, PlannerPhase.ClosedUp);
        public bool StepBegin => Step(PlannerPhase.Begin);
        public bool StepTarget => Step(PlannerPhase.Imaging) && !planner.IsTakingFrames;
        public bool StepImaging => Step(PlannerPhase.Imaging) && planner.IsTakingFrames;
        public bool StepEnd => Step(PlannerPhase.End);

        private void ServiceChanged(string property) {
            switch (property) {
                case nameof(PlannerService.IsRunning):
                case nameof(PlannerService.Phase):
                case nameof(PlannerService.StatusText):
                case nameof(PlannerService.IsTakingFrames):
                case nameof(PlannerService.IsPaused):
                case nameof(PlannerService.PausePending):
                case nameof(PlannerService.PausePoint):
                case nameof(PlannerService.PauseNote):
                    RefreshSafety();
                    RaiseStatus();
                    RaisePropertyChanged(nameof(ListState));
                    CommandManager.InvalidateRequerySuggested();
                    break;
                case nameof(PlannerService.CurrentTarget):
                    RaisePropertyChanged(nameof(ListState));
                    break;
                case nameof(PlannerService.List):
                    WatchTargets();
                    RaisePropertyChanged(nameof(Targets));
                    RaisePropertyChanged(nameof(ListName));
                    SelectedTarget = Targets.FirstOrDefault();
                    RaiseTargets();
                    break;
                case nameof(PlannerService.AutofocusPending):
                    RaisePropertyChanged(nameof(AutofocusText));
                    break;
                case nameof(PlannerService.EndProblems):
                    RaisePropertyChanged(nameof(EndProblems));
                    break;
                case nameof(PlannerService.ListName):
                case nameof(PlannerService.ListPath):
                case nameof(PlannerService.ListDirty):
                    RaisePropertyChanged(nameof(ListName));
                    RaisePropertyChanged(nameof(ListSavedText));
                    break;
            }
        }

        private void RaiseStatus() {
            foreach (var p in new[] { nameof(IsRunning), nameof(RunButtonText), nameof(RunButtonTip), nameof(StopShown), nameof(IsPaused), nameof(PauseShown), nameof(PausingShown),
                nameof(PauseEnabled), nameof(PauseTip),
                nameof(StatusText), nameof(PillText), nameof(PillBrush),
                nameof(StepWait), nameof(StepBegin), nameof(StepTarget), nameof(StepImaging), nameof(StepEnd) }) {
                RaisePropertyChanged(p);
            }
        }

        private void RefreshSafety() {
            bool? now = null;
            try {
                var info = planner.Nina.SafetyMonitor.GetInfo();
                if (info?.Connected == true) { now = info.IsSafe; }
            } catch (Exception) { }
            if (now != safe) {
                safe = now;
                RaisePropertyChanged(nameof(PillText));
                RaisePropertyChanged(nameof(PillBrush));
            }
        }

        // ---------- targets ----------
        public string ListName => planner.ListName;
        public string ListSavedText => planner.ListPath == null ? "Not saved yet" : planner.ListDirty ? "● Saving…" : "✔ Saved automatically";

        public IReadOnlyList<string> FilterChoices => planner.FilterNames().DefaultIfEmpty("L").ToList();
        public IReadOnlyList<string> BinningChoices { get; }
        public IReadOnlyList<Choice> ExposureTypeChoices { get; }

        public PlannerTarget SelectedTarget {
            get => selectedTarget;
            set {
                selectedTarget = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(HasSelection));
                RaisePropertyChanged(nameof(FilterChoices));
                RaiseDetail();
            }
        }
        public bool HasSelection => SelectedTarget != null;

        /// <summary>For the target list icons: the target being imaged now and the one the run is paused on.</summary>
        public TargetListState ListState => new(
            planner.IsRunning && planner.Phase == PlannerPhase.Imaging ? planner.CurrentTarget?.Id : null,
            planner.IsPaused && !planner.IsRunning ? planner.PausePoint?.TargetId : null);

        /// <summary>The target the loop takes next: the first checked target that is not complete.</summary>
        public PlannerTarget NextTarget => Targets.FirstOrDefault(t => t.Enabled && !t.IsComplete);

        public string CoordinatesText => SelectedTarget == null ? "" : $"RA {CoordinateText.FormatRa(SelectedTarget.RaHours)} · Dec {CoordinateText.FormatDec(SelectedTarget.DecDegrees)}";

        public IReadOnlyList<string> TargetChips {
            get {
                var t = SelectedTarget;
                if (t == null) { return Array.Empty<string>(); }
                var chips = new List<string> {
                    t.OnStart switch { OnStartAction.SlewAndCenter => "Slew and center", OnStartAction.SlewOnly => "Slew only", _ => "Stays where it is" },
                    t.Start.Enabled ? "starts at " + Constraint(t.Start) : WithSafety ? "starts when safe" : "starts when dark",
                    t.End.Enabled ? "ends at " + Constraint(t.End) : WithSafety ? "runs until unsafe" : "runs until dawn"
                };
                if (t.Rotate) { chips.Add($"Rotate {t.PositionAngle:0.#}°"); }
                return chips;
            }
        }

        private static string Constraint(TimeConstraint c) => c.By == ConstraintBy.Altitude ? $"{c.Altitude:0.#}° altitude" : c.Time.ToString(@"hh\:mm");

        public string RemainingText => SelectedTarget == null ? "" : $"{SelectedTarget.DoneFrames} of {SelectedTarget.TotalFrames} frames · {SelectedTarget.HoursRemaining:0.0} h remaining";

        public string RotateAfWarning { get => rotateAfWarning; private set { rotateAfWarning = value; RaisePropertyChanged(); } }

        private void RaiseDetail() {
            RaisePropertyChanged(nameof(CoordinatesText));
            RaisePropertyChanged(nameof(TargetChips));
            RaisePropertyChanged(nameof(RemainingText));
        }

        private void RaiseTargets() {
            RaisePropertyChanged(nameof(NextTarget));
            RaiseDetail();
        }

        // ---------- equipment & safety ----------
        public IReadOnlyList<KeepItem> KeepItems { get; }
        public IReadOnlyList<string> KeepChips => KeepItems.Where(k => k.IsChecked).Select(k => k.Label).DefaultIfEmpty("None").ToList();
        public ObservableCollection<StageView> Stages { get; }
        public string ModeSummary => WithSafety ? "Run with safety" : "Run without safety";
        public string CycleHint => WithSafety
            ? "Each stage is an Advanced Sequencer instruction set: edit it in NINA's sequencer with any instruction, including ones from other plugins. Unsafe at any point jumps to 4; after 4 the planner waits for safe and starts again at 1."
            : "Each stage is an Advanced Sequencer instruction set: edit it in NINA's sequencer with any instruction, including ones from other plugins. The run goes through your targets once and ends with 4.";

        public string WorkflowName => Options.WorkflowName ?? (planner.ActiveContainer == null ? "No workflow loaded" : "Untitled workflow");
        public string WorkflowSavedText => planner.ActiveContainer == null ? "Use \"Load a default workflow…\" below"
            : Options.WorkflowName != null && File.Exists(workflows.PathFor(Options.WorkflowName)) ? "Saved in Workflows" : "Not saved yet";

        private void RaiseWorkflow() {
            RaisePropertyChanged(nameof(WorkflowName));
            RaisePropertyChanged(nameof(WorkflowSavedText));
        }

        // ---------- options ----------
        public bool WithSafety { get => Options.RunMode == RunMode.WithSafety; set { if (value) { ChangeMode(RunMode.WithSafety); } } }
        public bool WithoutSafety { get => Options.RunMode == RunMode.WithoutSafety; set { if (value) { ChangeMode(RunMode.WithoutSafety); } } }
        /// <summary>"Close up and wait" is chosen: its time limit can be edited.</summary>
        public bool CloseUpOnUnsafe => Options.UnsafeAction == UnsafeAction.CloseUpAndWait;
        public IReadOnlyList<Choice> GapMountChoices { get; }
        public bool GapCloseDomeVisible => Options.GapMount == GapMountAction.StopTrackingAndPark;
        public IReadOnlyList<StepChip> GapWaitSteps => Chips(GapPlan.For(Options.GapMount, Options.GapCloseDome).Wait.Select(StepLabel));
        public IReadOnlyList<StepChip> GapResumeSteps => Chips(GapPlan.For(Options.GapMount, Options.GapCloseDome).Resume.Select(StepLabel).Append("2 Start of target"));
        public string GapResumeLabel => "At the next target's start time";
        public string GapHint => $"Shorter waits: guiding stops and the mount keeps tracking. Without a later target tonight the planner runs 4 End instead.";

        private static IReadOnlyList<StepChip> Chips(IEnumerable<string> steps) {
            var list = steps.ToList();
            return list.Select((s, i) => new StepChip(s, i < list.Count - 1)).ToList();
        }

        private static string StepLabel(GapStep s) => s switch {
            GapStep.StopGuiding => "Stop Guiding", GapStep.StopTracking => "Set Tracking · Stopped", GapStep.Park => "Park Scope",
            GapStep.FindHome => "Find Home", GapStep.CloseDome => "Close Dome Shutter", GapStep.OpenDome => "Open Dome Shutter", _ => "Unpark Scope"
        };

        public ICommand RunCommand { get; }
        public ICommand AddEmptyCommand { get; }
        public ICommand AddFromFramingCommand { get; }
        public ICommand AddMosaicCommand { get; }
        public ICommand DuplicateCommand { get; }
        public ICommand MoveUpCommand { get; }
        public ICommand MoveDownCommand { get; }
        public ICommand DeleteCommand { get; }
        public ICommand TargetSettingsCommand { get; }
        public ICommand AddExposureCommand { get; }
        public ICommand DeleteExposureCommand { get; }
        public ICommand ExposureUpCommand { get; }
        public ICommand ExposureDownCommand { get; }
        public ICommand ResetProgressCommand { get; }
        public ICommand NewListCommand { get; }
        public ICommand OpenListCommand { get; }
        public ICommand SaveListCommand { get; }
        public ICommand SaveListAsCommand { get; }
        public ICommand OpenWorkflowCommand { get; }
        public ICommand SaveWorkflowCommand { get; }
        public ICommand SaveWorkflowAsCommand { get; }
        public ICommand LoadDefaultWorkflowCommand { get; }
        public ICommand EditInSequencerCommand { get; }
        public ICommand RefreshStagesCommand { get; }
        public ICommand PauseNowCommand { get; }
        public ICommand PauseAfterFrameCommand { get; }
        public ICommand CancelPauseCommand { get; }
        public ICommand ResumeCommand { get; }
        public ICommand AutofocusCommand { get; }
        public string AutofocusText => planner.AutofocusPending ? "◎  Autofocus requested" : "◎  Autofocus";
        public ICommand GoOptionsCommand { get; }
        public ICommand GoInfoCommand { get; }
        public ICommand ApplyDefaultsToAllCommand { get; }
        public ICommand ResetConfirmationsCommand { get; }

        /// <summary>Test hook: opens a dialog by name (it stays open until "close dialogs").</summary>
        private void OpenDialog(string name) {
            switch (name) {
                case "target settings": TargetSettingsCommand.Execute(SelectedTarget); break;
                case "planning tools": new PlanningToolsWindow(new TargetSettingsVM(planner, SelectedTarget, isNew: false)).ShowDialog(); break;
                case "load default": ChooseDefaultWorkflow(); break;
                case "delete target": DeleteTarget(); break;
                case "open list": OpenList(); break;
                case "report stages":
                    for (var n = 0; n < 4; n++) {
                        planner.Log.Info($"Panel stage {n + 1}: " + string.Join(" | ", Stages[n].Lines.Select(l => l.Name + l.SummaryText)));
                    }
                    break;
                default:
                    var parts = name.Split(' ');
                    if (parts.Length == 3 && int.TryParse(parts[2], out var i) && i >= 1 && i <= Targets.Count) {
                        var target = Targets[i - 1];
                        SelectedTarget = target;
                        if (parts[0] == "rename") { target.Name += " (renamed)"; }
                        if (parts[0] is "slew" or "center") {
                            var vm = new TargetSettingsVM(planner, target, isNew: false);
                            _ = vm.SlewNowAsync(center: parts[0] == "center");
                        }
                    }
                    break;
            }
        }

        // ---------- run ----------
        private async void Run() {
            if (planner.IsRunning || planner.IsPaused) {
                var (yes, _) = Ask.Confirm("Stop", "Stop the run? The frame being taken is dropped, then 4 End runs: park, close the dome, warm the camera and switch power off.", yes: "Stop and run 4 End", offerDontAsk: false);
                if (yes) { planner.Stop(); }
                return;
            }
            if (workflows.CurrentRoot() == null) {
                var name = WorkflowLibrary.DefaultFor(Options.RunMode);
                var answer = Ask.Choose("No workflow loaded",
                    $"No Observatory Planner workflow is loaded in the Advanced Sequencer. Load the \"{name}\" workflow and run? It replaces the sequence that is open in the Advanced Sequencer.",
                    Ask.Primary("Load and run", "run"), Ask.Cancel());
                if (answer != "run") { return; }
                if (!await LoadWorkflow(name, askToSave: false)) { return; }
            }
            planner.StartRun(StartKind.Normal);
        }

        // ---------- targets ----------
        private void AddEmpty() {
            var t = planner.NewTarget();
            Targets.Add(t);
            SelectedTarget = t;
            if (!OpenTargetSettings(t, isNew: true)) {
                Targets.Remove(t);
                SelectedTarget = Targets.LastOrDefault();
            }
        }

        /// <summary>Opens the ⚙ Target Settings popup. Returns false when it was cancelled.</summary>
        private bool OpenTargetSettings(PlannerTarget t, bool isNew) {
            if (t == null) { return false; }
            SelectedTarget = t;
            var ok = new TargetSettingsWindow(new TargetSettingsVM(planner, t, isNew)).ShowDialog() == true;
            RaiseTargets();
            return ok;
        }

        private void AddFromFraming(bool mosaic) {
            var framing = planner.Nina.FramingAssistant;
            var rects = framing?.CameraRectangles?.ToList();
            if (framing == null || !framing.RectangleCalculated || rects == null || rects.Count == 0) {
                Ask.Info("Add from Framing Assistant", "Frame a target in NINA's Framing tab first: search a target and load the image.");
                return;
            }
            var name = framing.DSO?.Name;
            if (string.IsNullOrWhiteSpace(name)) { name = "Framed target"; }
            // the camera rectangles carry the position angle; the outer Rectangle's DSOPositionAngle is never set by NINA
            if (!mosaic) { rects = rects.Take(1).ToList(); }
            PlannerTarget first = null;
            foreach (var r in rects) {
                var t = planner.NewTarget(rects.Count > 1 ? $"{name} {r.Name}" : name);
                t.RaHours = r.Coordinates.RA;
                t.DecDegrees = r.Coordinates.Dec;
                t.PositionAngle = r.DSOPositionAngle;
                t.Rotate = Math.Abs(r.DSOPositionAngle) > 0.01;
                Targets.Add(t);
                first ??= t;
            }
            SelectedTarget = first;
        }

        private void Duplicate() {
            var copy = SelectedTarget.Clone(keepProgress: false);
            copy.Name += " (copy)";
            Targets.Insert(Targets.IndexOf(SelectedTarget) + 1, copy);
            SelectedTarget = copy;
        }

        private void MoveTarget(int direction) {
            var i = Targets.IndexOf(SelectedTarget);
            var j = i + direction;
            if (i < 0 || j < 0 || j >= Targets.Count) { return; }
            Targets.Move(i, j);
        }

        private void DeleteTarget() {
            var t = SelectedTarget;
            if (Options.ConfirmDeleteTarget) {
                var (yes, dontAsk) = Ask.Confirm("Delete target",
                    $"Delete \"{t.Name}\"? Its {t.Exposures.Count} exposure rows and progress ({t.DoneFrames} of {t.TotalFrames} frames) will be removed.");
                if (!yes) { return; }
                if (dontAsk) { Options.ConfirmDeleteTarget = false; }
            }
            var i = Targets.IndexOf(t);
            Targets.Remove(t);
            SelectedTarget = Targets.Count == 0 ? null : Targets[Math.Min(i, Targets.Count - 1)];
        }

        private void AddExposure() {
            var last = SelectedTarget.Exposures.LastOrDefault();
            SelectedTarget.Exposures.Add(last != null ? last.Clone(keepProgress: false) : new PlannerExposure { Filter = FilterChoices.First() });
        }

        private void DeleteExposure(object p) {
            if (p is not PlannerExposure e || SelectedTarget == null) { return; }
            if (Options.ConfirmDeleteExposure) {
                var (yes, dontAsk) = Ask.Confirm("Delete exposure",
                    $"Delete the {e.Filter} {e.ExposureTime:0.#} s row from \"{SelectedTarget.Name}\"?" + (e.Done > 0 ? $" Its {e.Done} captured frames will no longer be counted." : ""));
                if (!yes) { return; }
                if (dontAsk) { Options.ConfirmDeleteExposure = false; }
            }
            SelectedTarget.Exposures.Remove(e);
        }

        private void MoveExposure(object p, int direction) {
            if (p is not PlannerExposure e || SelectedTarget == null) { return; }
            var list = SelectedTarget.Exposures;
            var i = list.IndexOf(e);
            var j = i + direction;
            if (i >= 0 && j >= 0 && j < list.Count) { list.Move(i, j); }
        }

        private void ResetProgress() {
            var (yes, _) = Ask.Confirm("Reset progress", $"Reset the progress of \"{SelectedTarget.Name}\" to 0 frames?", yes: "Reset", offerDontAsk: false);
            if (!yes) { return; }
            foreach (var e in SelectedTarget.Exposures) { e.Done = 0; }
        }

        private void ApplyDefaultsToAll() {
            var (yes, _) = Ask.Confirm("Apply to all targets",
                $"Set Delay first {Options.DefaultDelayFirst:0.#} s, Delay between {Options.DefaultDelayBetween:0.#} s and " +
                $"{(Options.DefaultOrder == ExposureOrder.RotateThroughFilters ? "Rotate through filters" : "Finish each row first")} on all {Targets.Count} targets?",
                yes: "Apply", offerDontAsk: false);
            if (!yes) { return; }
            foreach (var t in Targets) {
                t.DelayFirst = Options.DefaultDelayFirst;
                t.DelayBetween = Options.DefaultDelayBetween;
                t.Order = Options.DefaultOrder;
            }
        }

        // ---------- target list files ----------
        private void NewList() {
            planner.SaveTargets();
            planner.NewTargets();
        }

        private void OpenList() {
            var dlg = new LibraryWindow("Open target list", "target lists", planner.Store.TargetsFolder, planner.ListPath, path => {
                var list = planner.Store.LoadTargetList(path);
                return $"{list.Targets.Count} target{(list.Targets.Count == 1 ? "" : "s")}";
            });
            if (dlg.ShowDialog() != true || dlg.Chosen == null) { return; }
            planner.SaveTargets();
            try { planner.OpenTargets(dlg.Chosen); } catch (Exception ex) { Ask.Info("Open target list", $"Could not open the target list: {ex.Message}"); }
        }

        private void SaveList() {
            if (planner.ListPath == null) { SaveListAs(); return; }
            planner.SaveTargets();
        }

        private void SaveListAs() {
            var name = Ask.Text("Save target list as", "Name", ListName == "Untitled" ? "" : ListName, ValidName);
            if (name == null) { return; }
            var path = planner.DefaultPathFor(name);
            if (File.Exists(path) && !string.Equals(path, planner.ListPath, StringComparison.OrdinalIgnoreCase)
                && Ask.Choose("Save target list as", $"\"{name}\" already exists. Replace it?", Ask.Danger("Replace", "yes"), Ask.Cancel()) != "yes") { return; }
            planner.SaveTargets(path, name);
        }

        private static string ValidName(string name) =>
            string.IsNullOrWhiteSpace(name) ? "Enter a name." : name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ? "A name can't contain \\ / : * ? \" < > |" : null;

        private readonly HashSet<object> watched = new();

        /// <summary>Saves the target list a moment after any edit (progress is also saved after every frame).</summary>
        private void WatchTargets() {
            void Changed(object s, EventArgs e) {
                Ui.Run(() => {
                    RaiseTargets();
                    UpdateRotateWarning();
                    saveTimer?.Stop();
                    saveTimer?.Start();
                });
            }
            void Watch(PlannerTarget t) {
                if (!watched.Add(t)) { return; }
                t.PropertyChanged += (s, e) => Changed(s, e);
                t.Start.PropertyChanged += (s, e) => Changed(s, e);
                t.End.PropertyChanged += (s, e) => Changed(s, e);
                t.Exposures.CollectionChanged += (s, e) => Changed(s, e);
            }
            foreach (var t in Targets) { Watch(t); }
            Targets.CollectionChanged += (s, e) => {
                foreach (var t in Targets) { Watch(t); }
                Changed(s, e);
            };
            UpdateRotateWarning();
        }

        private void UpdateRotateWarning() {
            var triggers = WorkflowLibrary.Describe(planner.ActiveContainer?.Stage(StageKind.Triggers));
            RotateAfWarning = StageChecks.RotateWithFilterAf(Targets, triggers);
        }

        // ---------- workflow (the four stages) ----------
        private async void ChooseDefaultWorkflow() {
            var choices = WorkflowLibrary.Names.Select(n => new AskChoice {
                Name = n, Description = WorkflowLibrary.Notes.TryGetValue(n, out var note) ? note : "", IsSelected = n == WorkflowLibrary.DefaultFor(Options.RunMode)
            }).ToList();
            var (result, choice) = Ask.Pick("Load a default workflow",
                "Replaces the four stages with a starter set. You can edit every instruction afterwards in the Advanced Sequencer.",
                choices, Ask.Primary("Load", "load"), Ask.Cancel());
            if (result != "load" || choice == null) { return; }
            await LoadWorkflow(choice, askToSave: true);
        }

        private async System.Threading.Tasks.Task<bool> LoadWorkflow(string name, bool askToSave) {
            if (name == null) { return false; }
            if (askToSave && !SaveBeforeReplace()) { return false; }
            await ConnectSwitchHub();
            var container = WorkflowLibrary.Build(name, planner);
            planner.Nina.Sequence.SetAdvancedSequence(WorkflowLibrary.NewSequence(container));
            if (Options.RunMode != WorkflowLibrary.ModeOf(name)) { Options.RunMode = WorkflowLibrary.ModeOf(name); }
            Options.WorkflowName = name;
            RefreshStages();
            RaiseWorkflow();
            return true;
        }

        /// <summary>
        /// The default workflows name their power switches; NINA stores a switch by its position on the hub, which is only
        /// known while the hub is connected. The hub stays connected during runs anyway (Keep connected).
        /// </summary>
        private async System.Threading.Tasks.Task ConnectSwitchHub() {
            try {
                if (planner.Nina.Switches.GetInfo()?.Connected == true) { return; }
                var connect = planner.Nina.Switches.Connect();
                await System.Threading.Tasks.Task.WhenAny(connect, System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(30)));
            } catch (Exception ex) { Logger.Error(ex); }
            if (planner.Nina.Switches.GetInfo()?.Connected != true) {
                Ask.Info("Switch hub not connected",
                    "The switch hub could not be connected, so the power switches are set by position (1st to 7th switch). Check each Set Switch Value in the Advanced Sequencer, or connect the hub and load the workflow again.");
            }
        }

        /// <summary>Offers to save the current workflow before it is replaced. False when the user cancelled.</summary>
        private bool SaveBeforeReplace() {
            if (workflows.CurrentRoot() == null) { return true; }
            var answer = Ask.Choose("Save the current workflow?",
                "Save the four stages you have now before they are replaced?",
                Ask.Primary("Save as…", "save"), Ask.Plain("Don't save", "skip"), Ask.Cancel());
            if (answer == "cancel") { return false; }
            return answer != "save" || SaveWorkflow(askName: true);
        }

        private bool SaveWorkflow(bool askName) {
            var name = Options.WorkflowName;
            if (askName || name == null || WorkflowLibrary.Names.Contains(name)) {
                name = Ask.Text("Save workflow as", "Name", name != null && !WorkflowLibrary.Names.Contains(name) ? name : "My observatory", ValidName);
                if (name == null) { return false; }
                if (File.Exists(workflows.PathFor(name)) && name != Options.WorkflowName
                    && Ask.Choose("Save workflow as", $"\"{name}\" already exists. Replace it?", Ask.Danger("Replace", "yes"), Ask.Cancel()) != "yes") { return false; }
            }
            try {
                workflows.Save(name).GetAwaiter().GetResult();
                RaiseWorkflow();
                return true;
            } catch (Exception ex) {
                Logger.Error(ex);
                Ask.Info("Save workflow", $"Could not save the workflow: {ex.Message}");
                return false;
            }
        }

        private void OpenWorkflow() {
            var dlg = new LibraryWindow("Open workflow", "workflows", workflows.Folder, Options.WorkflowName == null ? null : workflows.PathFor(Options.WorkflowName), null);
            if (dlg.ShowDialog() != true || dlg.Chosen == null) { return; }
            if (!SaveBeforeReplace()) { return; }
            try {
                if (!workflows.Open(dlg.Chosen)) {
                    Ask.Info("Open workflow", "This version of N.I.N.A. does not let the planner open sequence files. Open it in NINA's Sequencer instead (Advanced Sequencer › Open).");
                    return;
                }
            } catch (Exception ex) {
                Logger.Error(ex);
                Ask.Info("Open workflow", $"Could not open the workflow: {ex.InnerException?.Message ?? ex.Message}");
                return;
            }
            RefreshStages();
            RaiseWorkflow();
        }

        private void EditInSequencer() {
            try {
                planner.Nina.Application.ChangeTab(ApplicationTab.SEQUENCE);
                planner.Nina.Sequence.SwitchToAdvancedView();
            } catch (Exception ex) { Logger.Error(ex); }
        }

        private void ChangeMode(RunMode mode) {
            if (Options.RunMode == mode) { return; }
            if (planner.IsRunning) {
                Ask.Info("Run mode", "Stop the run before changing the run mode.");
                RaiseModeProperties();
                return;
            }
            var defaultName = WorkflowLibrary.DefaultFor(mode);
            var answer = Ask.Choose(mode == RunMode.WithSafety ? "Run with safety" : "Run without safety",
                $"Load the default workflow for running {(mode == RunMode.WithSafety ? "with" : "without")} a safety monitor (\"{defaultName}\")? It replaces your four stages.",
                Ask.Primary("Load default workflow", "load"), Ask.Plain("Keep my stages", "keep"), Ask.Cancel());
            if (answer == "cancel") { RaiseModeProperties(); return; }
            Options.RunMode = mode;
            if (answer == "load") { _ = LoadWorkflow(defaultName, askToSave: true); }
        }

        private void RaiseModeProperties() {
            foreach (var p in new[] { nameof(WithSafety), nameof(WithoutSafety), nameof(ModeSummary), nameof(CycleHint), nameof(TargetChips) }) { RaisePropertyChanged(p); }
            RaiseStatus();
        }

        private void OptionChanged(string property) {
            Ui.Run(() => {
                if (property == nameof(PlannerOptions.RunMode)) {
                    RaiseModeProperties();
                    foreach (var k in KeepItems) { k.Refresh(); }
                    RaisePropertyChanged(nameof(KeepChips));
                    RefreshStages();
                }
                if (property is nameof(PlannerOptions.GapMount) or nameof(PlannerOptions.GapCloseDome) or nameof(PlannerOptions.GapMinutes)) {
                    RaisePropertyChanged(nameof(GapCloseDomeVisible)); RaisePropertyChanged(nameof(GapWaitSteps)); RaisePropertyChanged(nameof(GapResumeSteps));
                }
                if (property == nameof(PlannerOptions.WorkflowName)) { RaiseWorkflow(); }
                if (property == nameof(PlannerOptions.GuidingLimitPixels)) { RaisePropertyChanged(nameof(GuidingLimitArcsec)); }
                if (property == nameof(PlannerOptions.UnsafeAction)) { RaisePropertyChanged(nameof(CloseUpOnUnsafe)); }
            });
        }

        public void RefreshStages() {
            // follow the block in the sequence that is open in the Advanced Sequencer (it may have been replaced or re-added)
            var block = workflows.FindBlock(out var known);
            if (known && !planner.IsRunning && !ReferenceEquals(block, planner.ActiveContainer)) {
                planner.Register(block);
                return; // Register raises ActiveContainerChanged, which refreshes again
            }
            var c = planner.ActiveContainer;
            var safety = WithSafety;
            string[] titles = {
                safety ? "Begin: when it becomes safe" : "Begin: when you press Run",
                "Start of each target",
                "While imaging: triggers",
                safety ? "End: when unsafe or finished" : "End: when finished"
            };
            string[] hints = {
                safety ? "Runs in the evening and after every weather interruption." : "Runs once at the start. The default workflows begin with Wait for Time (astronomical dusk).",
                "Runs when a target starts" + (safety ? ", and again when it resumes after unsafe." : ".") + " The target's \"When target starts\" choice replaces the Center step.",
                "Checked between exposures. Only triggers can go here.",
                safety ? "Runs on clouds, rain, dawn, or when every target is complete." : "Runs when every target is done or the last target's time window has ended, then the run stops."
            };
            for (var n = 1; n <= 4; n++) {
                var view = Stages[n - 1];
                var kind = (StageKind)n;
                view.Title = titles[n - 1];
                view.Hint = hints[n - 1];
                if (c == null) {
                    view.Lines = Array.Empty<StageLine>();
                    view.EmptyText = "No Observatory Planner workflow is loaded. Use \"Load a default workflow…\" above.";
                    view.Warnings = Array.Empty<string>();
                    continue;
                }
                var entries = WorkflowLibrary.Describe(c.Stage(kind));
                view.Lines = entries.Select((e, i) => new StageLine(i + 1, e.Category, e.Name, e.Summary)).ToList();
                view.EmptyText = entries.Count == 0 ? $"No {(kind == StageKind.Triggers ? "triggers" : "instructions")}. This stage does nothing." : null;
                view.Warnings = StageChecks.Check(kind, entries, Options);
            }
            UpdateRotateWarning();
        }
    }
}
