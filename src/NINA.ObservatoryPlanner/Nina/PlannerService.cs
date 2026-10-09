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
            GuideStarLost = ReadGuideStarLost;
            GuidingError = () => guideErrors.Total(DateTime.Now);
            if (nina?.Guider != null) { WatchGuideSteps(); }
            if (nina?.Guider != null && nina.Telescope != null && nina.Profile != null) { WatchDisconnects(); }
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

        private string endProblems;
        /// <summary>The 4 End steps that failed the last time it ran (shown in the panel until 4 End runs again), or null.</summary>
        public string EndProblems { get => endProblems; private set => Set(ref endProblems, value); }
        internal void SetEndProblems(string text) => EndProblems = text;
        /// <summary>Adds a problem to the note shown under the status bar (kept until 4 End runs again).</summary>
        internal void AddEndProblem(string text) => EndProblems = EndProblems == null ? text : EndProblems + " " + text;

        /// <summary>The filter of the last light frame (kept across targets, pauses and weather stops), or null.</summary>
        public string LastLightFilter { get; private set; }

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
            if (e.InEnd) { Log.Info("Pause ignored: 4 End is running and always runs to the end"); return; }
            if (!e.PauseAllowed) { Log.Info("Pause ignored: nothing runs yet (waiting for safe or for the next night); use Stop instead"); return; }
            e.RequestPause(kind);
            if (e.PausePending) {
                StatusText = kind == PauseKind.Now ? "Pausing now… (a meridian flip, the dome shutter or park finishes first)" : "Pausing after this step…";
            }
            Raise(nameof(PausePending));
        }

        /// <summary>Pause is possible: the run has started 1 Begin and 4 End is not running.</summary>
        public bool PauseAllowed => IsRunning && engine?.PauseAllowed == true;

        /// <summary>The target whose imaging has begun (2 Start of target ran to its end), or null.</summary>
        internal Guid? ImagingTargetId { get; set; }

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
            var found = await Ui.Run(() => { var block = Workflows.FindBlock(out _); if (block != null) { Register(block); } return block != null; });
            if (found) { await ShowAdvancedSequencerAsync(); }
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

        /// <summary>
        /// NINA's Sequencer tab opens on its overview page (unless the simple sequencer is turned off), so the restored
        /// workflow would only show after "Edit in Advanced Sequencer". NINA picks that page just after its sequencer
        /// reports ready, so wait a moment before switching.
        /// </summary>
        private async Task ShowAdvancedSequencerAsync() {
            await Task.Delay(TimeSpan.FromSeconds(2));
            await Ui.Run(() => {
                try { Nina.Sequence.SwitchToAdvancedView(); } catch (Exception ex) { Logger.Error(ex); }
            });
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
            var hardware = new NinaPlannerHardware(container, this, progress);
            var e = new PlannerEngine(Options, new TargetSelector(Site, Options),
                () => Ui.Run(() => Targets.ToList()).GetAwaiter().GetResult(),
                hardware, new NinaSafetySource(Nina), new SystemClock(), Log);
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
            } catch (OperationCanceledException) when (token.IsCancellationRequested && hardware.ErrorStop != null) {
                await EndAfterErrorStop(hardware, hardware.ErrorStop);
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

        /// <summary>
        /// An instruction failed with the error behaviour "Skip to end of sequence instructions" or "Abort", which stops
        /// NINA's whole sequence. For the planner that means: run 4 End (unless it was 4 End that failed) and stop, so the
        /// observatory is never left open and powered.
        /// </summary>
        private async Task EndAfterErrorStop(NinaPlannerHardware hardware, ErrorStop stop) {
            Log.Info($"\"{stop.Item}\" failed in {stop.Where}; its error behaviour \"{stop.BehaviorText}\" stopped NINA's sequence");
            if (!stop.InEnd) {
                Log.Phase(PlannerPhase.End, $"4 End (\"{stop.Item}\" failed)");
                // NINA's sequence is already cancelled, so 4 End runs on its own token
                try { await hardware.RunStage(StageKind.End, CancellationToken.None); } catch (Exception ex) {
                    Logger.Error(ex);
                    Log.Info($"4 End failed: {ex.Message}");
                }
            }
            IsPaused = false;
            PausePoint = null;
            SavePauseState();
            Log.Phase(PlannerPhase.Stopped, $"Stopped: \"{stop.Item}\" failed in {stop.Where} and its error behaviour is \"{stop.BehaviorText}\". "
                + (stop.InEnd ? "Check 4 End and the equipment." : "4 End has run.") + " Fix the problem and press Run again.");
        }

        internal void SetStatus(string message) => StatusText = message;

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

        // ---------- guide star lost ----------
        private bool guideStarGaveUp;

        /// <summary>True while the guider reports the guide star lost (PHD2's "LostLock"). False without a guider or for other guiders.</summary>
        internal Func<bool> GuideStarLost { get; set; }

        private bool ReadGuideStarLost() {
            try {
                if (Nina?.Guider?.GetInfo()?.Connected != true) { return false; }
                return Nina.Guider.GetDevice() is NINA.Equipment.Interfaces.IGuider g && g.State == "LostLock";
            } catch (Exception) { return false; }
        }

        private readonly GuideErrorWindow guideErrors = new();

        private void WatchGuideSteps() {
            try {
                Nina.Guider.GuideEvent += (_, step) => { if (step != null) { guideErrors.Add(DateTime.Now, step.RADistanceRaw, step.DECDistanceRaw); } };
                Nina.Guider.AfterDither += (_, _) => { guideErrors.Clear(); return Task.CompletedTask; };
                Nina.Guider.GuidingStarted += (_, _) => { guideErrors.Clear(); return Task.CompletedTask; };
            } catch (Exception ex) { Logger.Error(ex); }
        }

        /// <summary>
        /// ⚙ Options "Close PHD2" / "Close the mount software": hooked into NINA's own guider and mount disconnects, so they
        /// work with Disconnect Equipment and Disconnect All wherever those are. NINA waits for the handlers to finish.
        /// </summary>
        private void WatchDisconnects() {
            try {
                var closer = new DisconnectCloser(Options, SequenceRunning, () => IsDisconnectStepRunning("Mount"),
                    () => (Nina.Profile.ActiveProfile.GuiderSettings.GuiderName ?? "").StartsWith("PHD2", StringComparison.OrdinalIgnoreCase),
                    () => Nina.Guider.GetInfo()?.Connected == true, () => Nina.Guider.Disconnect(),
                    new Phd2Client(() => (Nina.Profile.ActiveProfile.GuiderSettings.PHD2ServerUrl, Nina.Profile.ActiveProfile.GuiderSettings.PHD2ServerPort)),
                    () => AscomDriverProgram.ExeFor(Nina.Profile.ActiveProfile.TelescopeSettings.Id), AscomDriverProgram.IsRunning,
                    (d, t) => Task.Delay(d, t), m => Log.Info(m), m => { Log.Info(m); Logger.Warning("Observatory Planner: " + m); AddEndProblem(m); });
                Closer = closer;
                if (Nina.Guider.GetInfo()?.Connected == true) { closer.GuiderConnected(); }
                if (Nina.Telescope.GetInfo()?.Connected == true) { closer.MountConnected(); }
                Nina.Guider.Connected += (_, _) => { closer.GuiderConnected(); return Task.CompletedTask; };
                Nina.Telescope.Connected += (_, _) => { closer.MountConnected(); return Task.CompletedTask; };
                mountConnectedForRecovery = Nina.Telescope.GetInfo()?.Connected == true;
                Nina.Telescope.Connected += (_, _) => { mountConnectedForRecovery = true; return Task.CompletedTask; };
                Nina.Telescope.Disconnected += (_, _) => { try { OnMountDisconnectedForRecovery(); } catch (Exception ex) { Logger.Error(ex); } return Task.CompletedTask; };
                Nina.Guider.Disconnected += (_, _) => closer.OnGuiderDisconnected();
                Nina.Telescope.Disconnected += (_, _) => closer.OnMountDisconnected();
            } catch (Exception ex) { Logger.Error(ex); }
        }

        // ---------- mount recovery (GS Server) ----------
        private bool mountPositionUnknown;
        private volatile bool mountConnectedForRecovery;

        /// <summary>The mount was lost while not parked: its position is unknown until AutoHome ran.</summary>
        public bool MountPositionUnknown { get => mountPositionUnknown; internal set => Set(ref mountPositionUnknown, value); }

        /// <summary>NINA's mount is GS Server (the recovery uses its AutoHome).</summary>
        public bool MountIsGss {
            get { try { return Nina?.Profile?.ActiveProfile?.TelescopeSettings?.Id == GssAutoHome.TelescopeId; } catch (Exception) { return false; } }
        }

        /// <summary>The position is unknown and the mount is not connected; a mount connected again (e.g. after AutoHome by hand) counts as known.</summary>
        internal bool MountPositionUnknownNow() {
            if (!MountPositionUnknown) { return false; }
            if (Nina?.Telescope?.GetInfo()?.Connected == true) {
                MountPositionUnknown = false;
                Log.Info("The mount is connected again: its position is taken as known");
                return false;
            }
            return true;
        }

        /// <summary>True (and noted) when the roof must stay open: the mount position is unknown and "close anyway" is off.</summary>
        internal bool RoofMustStayOpen(string what) {
            if (!MountPositionUnknown || Options.CloseRoofWhenRecoveryFails) { return false; }
            var text = $"{what}: the dome/roof was NOT closed, because the mount position is unknown (it may not be clear of the roof). Close it yourself, or turn on \"Close the dome/roof anyway\".";
            Log.Info(text);
            AddEndProblem(text);
            return true;
        }

        /// <summary>4 End with the mount position unknown and "close anyway" off: its Close Dome steps are skipped (this run only).</summary>
        internal void SkipRoofIfMountUnknown(NINA.Sequencer.Container.ISequenceContainer stage) {
            var closes = NinaPlannerHardware.Descendants(stage).Where(i => i is NINA.Sequencer.SequenceItem.Dome.CloseDomeShutter && i.Status == NINA.Core.Enum.SequenceEntityStatus.CREATED).ToList();
            if (closes.Count == 0 || !RoofMustStayOpen("4 End")) { return; }
            foreach (var c in closes) { c.Status = NINA.Core.Enum.SequenceEntityStatus.SKIPPED; }
        }

        /// <summary>A Disconnect Equipment step for this device (or Disconnect All) is running in NINA's sequence.</summary>
        internal bool IsDisconnectStepRunning(string device) {
            try {
                if (Workflows.CurrentRoot() is not NINA.Sequencer.Container.ISequenceRootContainer root) { return false; }
                return root.GetCurrentRunningItems().Any(i => i.GetType().Name == "DisconnectAllEquipment"
                    || (InstructionFactory.DeviceOf(i) == device && i is not NINA.Sequencer.SequenceItem.Connect.ConnectEquipment));
            } catch (Exception) { return false; }
        }

        /// <summary>The mount disconnected: when no Disconnect step asked for it, GS Server stopped; recover or note it.</summary>
        private void OnMountDisconnectedForRecovery() {
            if (!mountConnectedForRecovery) { return; } // NINA's disconnect before connecting
            mountConnectedForRecovery = false;
            if (IsDisconnectStepRunning("Mount")) { return; }
            if (!MountIsGss || !Options.MountRecovery) { return; }
            if (!IsRunning && !IsPaused) { return; }
            if (Phase is PlannerPhase.ClosedUp or PlannerPhase.WaitingForSafe or PlannerPhase.WaitingForNextNight) { return; } // parked
            MountPositionUnknown = true;
            Log.Info("Mount connection lost without a Disconnect step (GS Server may have stopped): the mount position is unknown");
            var e = engine;
            if (IsRunning && e != null && !e.InEnd && e.PauseAllowed) {
                e.MountLost();
            } else if (e?.InEnd == true) {
                AddEndProblem("Mount connection lost during 4 End: the mount position is unknown.");
                var end = ActiveContainer?.Stage(StageKind.End);
                if (end != null) { _ = Ui.Run(() => SkipRoofIfMountUnknown(end)); }
            } else {
                AddEndProblem("Mount connection lost while paused: Start sequence recovers the mount (reconnect, AutoHome) before it continues.");
            }
        }

        /// <summary>Closes PHD2 / the mount software on disconnect (null without NINA).</summary>
        internal DisconnectCloser Closer { get; private set; }

        /// <summary>NINA's Advanced Sequencer is running (the planner, or anything else in the sequence).</summary>
        private bool SequenceRunning() {
            try { return IsRunning || Nina.Sequence.IsAdvancedSequenceRunning(); } catch (Exception) { return IsRunning; }
        }

        /// <summary>The guiding error of the last 10 guide steps in guide camera pixels, or null when unknown.</summary>
        internal Func<double?> GuidingError { get; set; }

        /// <summary>True when the guiding check is on and the guiding error is known and above the limit.</summary>
        internal bool GuidingErrorAbove() => Options.GuidingCheck && GuidingError() is double e && e > Options.GuidingLimitPixels;

        /// <summary>PHD2's pixel scale (arcseconds per guide camera pixel), or null when the guider is not connected.</summary>
        public double? GuiderPixelScale {
            get {
                try {
                    var info = Nina?.Guider?.GetInfo();
                    return info?.Connected == true && info.PixelScale > 0 ? info.PixelScale : null;
                } catch (Exception) { return null; }
            }
        }

        /// <summary>The imaging loop gave up waiting for the guide star.</summary>
        internal void GaveUpOnGuideStar() => guideStarGaveUp = true;

        /// <summary>Takes the "gave up on the guide star" note, if any (the target run then reports it to the engine).</summary>
        internal bool TakeGuideStarGaveUp() {
            var v = guideStarGaveUp;
            guideStarGaveUp = false;
            return v;
        }

        // ---------- autofocus: after 1 Begin and when the filter changes ----------
        private bool focusAfterBeginDue;
        private PlannerExposure nextFrame;
        private bool focusedForNextFrame;

        /// <summary>1 Begin is starting: the equipment was off, so the focus has to be found again.</summary>
        internal void OnBeginStarting() => focusAfterBeginDue = true;

        /// <summary>4 End is starting: the next imaging always comes after 1 Begin again.</summary>
        internal void OnEndStarting() => focusAfterBeginDue = false;

        /// <summary>The imaging loop is about to take this frame (its filter is set; triggers come next).</summary>
        internal void PrepareFrame(PlannerExposure e) {
            nextFrame = e;
            focusedForNextFrame = false;
        }

        /// <summary>True once, for the first light frame after 1 Begin, when "Autofocus before the first frame after 1 Begin" is on.</summary>
        internal bool TakeFocusAfterBegin(PlannerExposure e) {
            if (e.Type != ExposureType.Light || !focusAfterBeginDue) { return false; }
            focusAfterBeginDue = false;
            return Options.AutofocusAfterBegin;
        }

        /// <summary>An autofocus ran for the frame about to be taken: the filter-change trigger doesn't run it again.</summary>
        internal void MarkFocused() => focusedForNextFrame = true;

        /// <summary>
        /// For "AF after filter change (planner)": the next frame is a light frame with another filter than the last light
        /// frame (across targets, pauses and weather stops), and no autofocus ran for it yet.
        /// </summary>
        internal bool FilterChangeNeedsFocus() =>
            nextFrame is { Type: ExposureType.Light } f && !focusedForNextFrame && LastLightFilter != null
            && !string.Equals(f.Filter, LastLightFilter, StringComparison.OrdinalIgnoreCase);

        public void OnFrameStarting(PlannerTarget target, PlannerExposure e) {
            StatusText = $"Imaging {target.Name} · {e.Filter} {e.ExposureTime:0.#} s · frame {e.Done + 1} of {e.Count}";
            Log.Info($"Frame starting: {target.Name} {e.Filter} {e.ExposureTime:0.#} s{(e.Type == ExposureType.Light ? "" : " " + e.Type)}");
            IsTakingFrames = true;
            if (e.Type == ExposureType.Light) { LastLightFilter = e.Filter; }
            foreach (var x in target.Exposures) { x.IsActive = ReferenceEquals(x, e); }
        }

        /// <summary>The imaging of a target stopped (done, window closed, pause, weather): no row is active any more.</summary>
        public void OnImagingEnded(PlannerTarget target) {
            nextFrame = null;
            foreach (var x in target.Exposures) { x.IsActive = false; }
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
