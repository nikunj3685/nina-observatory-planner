using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.ObservatoryPlanner.Core;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.ObservatoryPlanner.Nina {

    /// <summary>
    /// Exports the one planner of the process. NINA composes the plugin manifest and the plugin's other parts
    /// (panel, sequencer blocks) in separate MEF containers, so a [Shared] part alone would exist twice.
    /// </summary>
    [PartCreationPolicy(CreationPolicy.Shared)]
    public class PlannerServiceExport {
        private static readonly object gate = new();
        private static PlannerService instance;

        [ImportingConstructor]
        public PlannerServiceExport(NinaServices nina) {
            lock (gate) { instance ??= new PlannerService(nina); }
        }

        [Export(typeof(PlannerService))]
        public PlannerService Service => instance;
    }

    /// <summary>Shared state of the planner: options, the target list, the running state and the registered sequencer block.</summary>
    public class PlannerService : Observable {
        private readonly object saveLock = new();
        private TargetList list;
        private string listPath;
        private bool isRunning;
        private PlannerPhase phase = PlannerPhase.Idle;
        private string statusText = "Stopped";
        private PlannerTarget currentTarget;
        private bool listDirty;
        private bool isTakingFrames;

        public PlannerService(NinaServices nina) : this(nina, PlannerStore.DefaultRoot()) { }

        public PlannerService(NinaServices nina, string storeRoot) {
            Nina = nina;
            BaseRoot = storeRoot;
            Store = PlannerStore.ForProfile(storeRoot, ProfileId());
            Options = Store.LoadOptions();
            Options.PropertyChanged += (_, _) => SaveOptions();
            Options.KeepConnected.CollectionChanged += (_, _) => SaveOptions();
            Log = new PlannerLog(Path.Combine(BaseRoot, "Logs"), this);
            Factory = nina != null ? new InstructionFactory(nina) : null;
            Workflows = new WorkflowFiles(this);
            LoadListAndState();
            if (nina?.Profile != null) { nina.Profile.ProfileChanged += (_, _) => Ui.Run(SwitchProfile); }
        }

        private bool loadingOptions;

        private void SaveOptions() {
            if (!loadingOptions) { Store.SaveOptions(Options); }
        }

        private string ProfileId() {
            try { return Nina?.Profile?.ActiveProfile?.Id.ToString(); } catch (Exception) { return null; }
        }

        private void LoadListAndState() {
            TargetList loaded = null;
            string path = null;
            var last = Store.LastTargetListPath();
            if (last != null) {
                try { loaded = Store.LoadTargetList(last); path = last; } catch (Exception ex) { Logger.Error(ex); }
            }
            list = loaded ?? new TargetList { Name = "Untitled" };
            listPath = path;
            var state = Store.LoadState();
            pausePoint = state.Point;
            isPaused = state.Paused;
        }

        /// <summary>Each NINA profile has its own target list, workflow, options and paused state.</summary>
        private void SwitchProfile() {
            if (IsRunning) { Log.Info("Profile changed while running: the planner keeps the running profile's data until the run ends"); return; }
            try { SaveTargets(); } catch (Exception ex) { Logger.Error(ex); }
            Store = PlannerStore.ForProfile(BaseRoot, ProfileId());
            loadingOptions = true;
            try { Options.CopyFrom(Store.LoadOptions()); } finally { loadingOptions = false; }
            LoadListAndState();
            Workflows.Reset();
            Raise(nameof(List)); Raise(nameof(Targets)); Raise(nameof(ListName)); Raise(nameof(ListPath));
            Raise(nameof(IsPaused)); Raise(nameof(PausePoint));
            Log.Info($"Profile changed: planner data from {Store.Root}");
            _ = RestoreWorkflowAsync();
            if (IsPaused) { StartPauseWatch(); }
        }

        public NinaServices Nina { get; }
        /// <summary>The planner's folder; each profile's data is in Profiles\&lt;id&gt; under it, the logs in Logs.</summary>
        public string BaseRoot { get; }
        public PlannerStore Store { get; private set; }
        public PlannerOptions Options { get; }
        internal WorkflowFiles Workflows { get; }
        public PlannerLog Log { get; }
        public InstructionFactory Factory { get; }

        public TargetList List { get => list; private set { list = value; Raise(); Raise(nameof(Targets)); Raise(nameof(ListName)); } }
        public ObservableCollection<PlannerTarget> Targets => list.Targets;
        public string ListName => list.Name;
        public string ListPath { get => listPath; private set => Set(ref listPath, value); }
        public bool ListDirty { get => listDirty; set => Set(ref listDirty, value); }

        public bool IsRunning { get => isRunning; private set => Set(ref isRunning, value); }
        public PlannerPhase Phase { get => phase; private set => Set(ref phase, value); }
        public string StatusText { get => statusText; private set => Set(ref statusText, value); }
        public PlannerTarget CurrentTarget { get => currentTarget; private set => Set(ref currentTarget, value); }
        /// <summary>True while frames of the current target are being taken (after 2 Start of target has run).</summary>
        public bool IsTakingFrames { get => isTakingFrames; private set => Set(ref isTakingFrames, value); }

        /// <summary>Set by the panel: shows it (used by the test hook).</summary>
        public Action ShowPanelRequested { get; set; }
        /// <summary>Set by the panel: selects one of its tabs (used by the test hook).</summary>
        public Action<int> ShowPlannerTabRequested { get; set; }
        /// <summary>Set by the panel: opens one of its dialogs without waiting for it (used by the test hook).</summary>
        public Action<string> DialogRequested { get; set; }

        /// <summary>The Observatory Planner block in the current Advanced Sequencer sequence, if any.</summary>
        public ObservatoryPlannerContainer ActiveContainer { get; private set; }
        public event EventHandler ActiveContainerChanged;

        public void Register(ObservatoryPlannerContainer container) {
            if (ReferenceEquals(ActiveContainer, container)) { return; }
            ActiveContainer = container;
            ActiveContainerChanged?.Invoke(this, EventArgs.Empty);
        }

        public Site Site {
            get {
                var a = Nina?.Profile?.ActiveProfile?.AstrometrySettings;
                return new Site(a?.Latitude ?? 0, a?.Longitude ?? 0);
            }
        }

        // ---------- pause / resume / stop ----------
        private bool isPaused;
        private PausePoint pausePoint;
        private PlannerEngine engine;
        private StartKind pendingStart = StartKind.Normal;
        private string pauseNote;

        /// <summary>Paused: NINA's sequence is not running, the equipment stays on and the planner keeps its place.</summary>
        public bool IsPaused { get => isPaused; private set => Set(ref isPaused, value); }
        public PausePoint PausePoint { get => pausePoint; private set => Set(ref pausePoint, value); }
        public bool PausePending => engine?.PausePending == true;
        /// <summary>Extra information while paused (a missing device, the meridian), or null.</summary>
        public string PauseNote { get => pauseNote; private set => Set(ref pauseNote, value); }

        /// <summary>
        /// Pause now drops the frame being taken; Pause after frame lets it finish and download. Outside a frame (1 Begin,
        /// a slew, a wait) both wait for that step to finish; 4 End is never paused.
        /// </summary>
        public void RequestPause(PauseKind kind) {
            var e = engine;
            if (!IsRunning || e == null) { return; }
            e.RequestPause(kind == PauseKind.Now && IsTakingFrames ? PauseKind.Now : PauseKind.AfterFrame);
            StatusText = kind == PauseKind.Now && IsTakingFrames ? "Pausing now…" : "Pausing after the current frame…";
            Raise(nameof(PausePending));
        }

        public void CancelPause() {
            engine?.CancelPause();
            Raise(nameof(PausePending));
        }

        /// <summary>Start sequence after a pause: checks connections and safety, then continues where it stopped.</summary>
        public void Resume() {
            if (IsRunning || !IsPaused) { return; }
            StartRun(StartKind.Resume);
        }

        /// <summary>Stop: 4 End runs (park, close, warm, power off) and the run ends. While paused it starts a short run for 4 End.</summary>
        public void Stop() {
            if (IsRunning) {
                engine?.RequestStop();
                StatusText = "Stopping: 4 End runs…";
                return;
            }
            if (IsPaused) { StartRun(StartKind.StopFromPause); }
        }

        /// <summary>Starts NINA's Advanced Sequencer, which runs the planner block in the given way.</summary>
        public void StartRun(StartKind kind) {
            pendingStart = kind;
            // NINA only runs instructions that have not run yet: after a pause or a stop the planner block is "finished"
            if (Workflows.CurrentRoot() is NINA.Sequencer.Container.SequenceContainer root) { root.ResetAll(); }
            _ = Nina.Sequence.StartAdvancedSequence(true);
        }

        private void SavePauseState() {
            try { Store.SaveState(IsPaused ? PlannerState.From(PausePoint) : new PlannerState()); } catch (Exception ex) { Logger.Error(ex); }
        }

        private CancellationTokenSource pauseWatch;

        /// <summary>
        /// While paused nothing runs in NINA's sequencer, so the planner itself watches: unsafe weather runs 4 End (and
        /// later 1 Begin, still paused); near the meridian it warns, and stops tracking once the flip time is reached.
        /// </summary>
        private void StartPauseWatch() {
            pauseWatch?.Cancel();
            var cts = pauseWatch = new CancellationTokenSource();
            _ = Task.Run(async () => {
                var warned = false;
                while (!cts.IsCancellationRequested) {
                    try { await Task.Delay(PauseWatchInterval, cts.Token); } catch (OperationCanceledException) { return; }
                    if (!IsPaused || IsRunning || Nina == null) { continue; }
                    try {
                        if (Options.RunMode == RunMode.WithSafety) {
                            var safety = Nina.SafetyMonitor.GetInfo();
                            if (safety?.Connected == true && !safety.IsSafe) {
                                Log.Info("Unsafe while paused: 4 End runs; when it is safe again 1 Begin runs and the planner stays paused");
                                await Ui.Run(() => StartRun(StartKind.WeatherWhilePaused));
                                continue;
                            }
                        }
                        var scope = Nina.Telescope.GetInfo();
                        var action = MeridianGuard.Check(scope?.Connected == true && scope.TrackingEnabled, scope?.Connected == true ? scope.TimeToMeridianFlip : null);
                        if (action == MeridianGuard.Action.StopTracking) {
                            Nina.Telescope.SetTrackingEnabled(false);
                            PauseNote = "The meridian flip time was reached while paused: tracking stopped so the mount can't hit the pier. Start sequence slews to the target again.";
                            Log.Info("Paused at the meridian flip time: tracking stopped");
                        } else if (action == MeridianGuard.Action.Warn && !warned) {
                            warned = true;
                            PauseNote = $"Meridian flip in {scope.TimeToMeridianFlipString}. If still paused then, tracking stops to protect the mount.";
                            Log.Info("Paused near the meridian: " + PauseNote);
                        }
                    } catch (Exception ex) { Logger.Error(ex); }
                }
            });
        }

        /// <summary>How often the pause watch looks at the weather and the mount.</summary>
        public TimeSpan PauseWatchInterval { get; set; } = TimeSpan.FromSeconds(5);

        // ---------- startup ----------

        /// <summary>
        /// At NINA start: reopen this profile's last workflow (the target list is already loaded), come back paused if it
        /// was paused, otherwise start the run when "Start Run forever when NINA starts" is on.
        /// </summary>
        public async Task RestoreAtStartupAsync() {
            for (var i = 0; i < 120 && !Nina.Sequence.Initialized; i++) { await Task.Delay(1000); }
            if (!Nina.Sequence.Initialized) { return; }
            await RestoreWorkflowAsync();
            await Ui.Run(() => { var block = Workflows.FindBlock(out _); if (block != null) { Register(block); } });
            if (IsPaused) {
                Log.Info($"NINA started while the sequence was paused{(PausePoint?.TargetName != null ? " on " + PausePoint.TargetName : "")}: still paused");
                SetPhase(PlannerPhase.Paused, "Paused (since before NINA was restarted). Press Start sequence to continue", null);
                StartPauseWatch();
                return;
            }
            if (Options.AutoStartOnLaunch && ActiveContainer != null) {
                Log.Info("Starting the run because \"Start Run forever when NINA starts\" is on");
                await Task.Delay(TimeSpan.FromSeconds(5));
                if (!IsRunning) { await Ui.Run(() => StartRun(StartKind.Normal)); }
            }
        }

        /// <summary>Puts this profile's last workflow into the Advanced Sequencer when the open sequence has no planner block.</summary>
        public async Task RestoreWorkflowAsync() {
            await Ui.Run(() => {
                try {
                    if (Workflows.FindBlock(out var known) != null || !known) { return; }
                    var name = Options.WorkflowName;
                    if (name == null) { return; }
                    var path = Workflows.PathFor(name);
                    if (File.Exists(path) && Workflows.Open(path)) {
                        Log.Info($"Workflow restored: {name}");
                    } else if (WorkflowLibrary.Names.Contains(name)) {
                        Nina.Sequence.SetAdvancedSequence(WorkflowLibrary.NewSequence(WorkflowLibrary.Build(name, this)));
                        Log.Info($"Workflow restored: {name} (built-in)");
                    }
                } catch (Exception ex) {
                    Logger.Error(ex);
                    Log.Info($"Could not restore the last workflow: {ex.Message}");
                }
            });
        }

        internal void SetPhase(PlannerPhase p, string message, PlannerTarget target) {
            IsTakingFrames = false;
            Phase = p;
            StatusText = message;
            if (p == PlannerPhase.Imaging || p == PlannerPhase.WaitingBetweenTargets) { CurrentTarget = target; }
            if (p is PlannerPhase.End or PlannerPhase.Finished or PlannerPhase.Stopped) { CurrentTarget = null; }
            if (p == PlannerPhase.Paused && target != null) { CurrentTarget = target; }
        }

        public async Task RunEngineAsync(ObservatoryPlannerContainer container, IProgress<ApplicationStatus> progress, CancellationToken token) {
            var start = pendingStart;
            pendingStart = StartKind.Normal;
            // a run started from NINA's own sequencer while paused continues the paused run
            if (start == StartKind.Normal && IsPaused) { start = StartKind.Resume; }
            pauseWatch?.Cancel();
            PauseNote = null;
            IsRunning = true;
            Log.StartRun(Options);
            var e = new PlannerEngine(Options, new TargetSelector(Site, Options),
                () => Ui.Run(() => Targets.ToList()).GetAwaiter().GetResult(),
                new NinaPlannerHardware(container, this, progress), new NinaSafetySource(Nina), new SystemClock(), Log);
            engine = e;
            if (start != StartKind.Normal) { Log.Info($"Run starts as: {start}"); }
            try {
                var outcome = await e.RunAsync(token, start, PausePoint);
                if (outcome == RunOutcome.Paused) {
                    PausePoint = e.Paused;
                    PauseNote = e.PausedReason;
                    IsPaused = true;
                    SavePauseState();
                    StartPauseWatch();
                } else {
                    IsPaused = false;
                    PausePoint = null;
                    SavePauseState();
                }
            } catch (OperationCanceledException) when (token.IsCancellationRequested) {
                // NINA's own Stop button: stops at once, without 4 End (the planner's Stop runs 4 End)
                Log.Phase(PlannerPhase.Stopped, "Stopped from NINA's sequencer (4 End did not run)");
                if (IsPaused) { StartPauseWatch(); }
            } catch (Exception ex) {
                Logger.Error("Observatory Planner stopped with an error", ex);
                Log.Phase(PlannerPhase.Stopped, $"Stopped with an error: {ex.Message}");
                throw;
            } finally {
                engine = null;
                IsRunning = false;
                Raise(nameof(PausePending));
            }
        }

        /// <summary>The running engine's check before each frame: false when a pause or stop is due.</summary>
        internal bool FrameAllowed => engine?.FrameAllowed() ?? true;

        private int autofocusRequested;

        /// <summary>The panel's Autofocus button: autofocus runs before the next frame (the frame being taken finishes first).</summary>
        public void RequestAutofocus() {
            if (Interlocked.Exchange(ref autofocusRequested, 1) == 0) {
                Log.Info("Autofocus requested: it runs before the next frame");
                Raise(nameof(AutofocusPending));
            }
        }

        public bool AutofocusPending => autofocusRequested == 1;

        /// <summary>Takes the pending autofocus request, if any (called by the imaging loop before each frame).</summary>
        internal bool TakeAutofocusRequest() {
            var taken = Interlocked.Exchange(ref autofocusRequested, 0) == 1;
            if (taken) { Raise(nameof(AutofocusPending)); }
            return taken;
        }

        public void OnFrameStarting(PlannerTarget target, PlannerExposure e) {
            StatusText = $"Imaging {target.Name} · {e.Filter} {e.ExposureTime:0.#} s · frame {e.Done + 1} of {e.Count}";
            Log.Info($"Frame starting: {target.Name} {e.Filter} {e.ExposureTime:0.#} s");
            IsTakingFrames = true;
        }

        /// <summary>Progress is saved after every frame so it survives clouds, dawn and NINA restarts.</summary>
        public void OnFrameTaken(PlannerTarget target, PlannerExposure exposure) {
            Log.Frame(target, exposure);
            SaveTargets();
        }

        public string DefaultPathFor(string name) {
            var safe = string.Concat((name ?? "Untitled").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            return Path.Combine(Store.TargetsFolder, safe + ".json");
        }

        public void SaveTargets(string path = null, string name = null) {
            lock (saveLock) {
                if (name != null) { list.Name = name; Raise(nameof(ListName)); }
                path ??= ListPath ?? DefaultPathFor(list.Name);
                Store.SaveTargetList(list, path);
                ListPath = path;
                ListDirty = false;
            }
        }

        public void OpenTargets(string path) {
            List = Store.LoadTargetList(path);
            ListPath = path;
            ListDirty = false;
            Store.SaveTargetList(List, path); // remember as last opened
        }

        public void NewTargets() {
            List = new TargetList { Name = "Untitled" };
            ListPath = null;
            ListDirty = false;
        }

        public PlannerTarget NewTarget(string name = "New target") => new PlannerTarget {
            Name = name, Order = Options.DefaultOrder, DelayFirst = Options.DefaultDelayFirst, DelayBetween = Options.DefaultDelayBetween,
            Exposures = new ObservableCollection<PlannerExposure> { new PlannerExposure { Filter = DefaultFilter(), ExposureTime = 120, Count = 50 } }
        };

        public IReadOnlyList<string> FilterNames() =>
            Nina?.Profile?.ActiveProfile?.FilterWheelSettings?.FilterWheelFilters?.Select(f => f.Name).ToList() ?? new List<string>();

        private string DefaultFilter() => FilterNames().FirstOrDefault() ?? "L";
    }
}
