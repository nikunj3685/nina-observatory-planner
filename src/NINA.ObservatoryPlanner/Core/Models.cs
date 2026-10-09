using Newtonsoft.Json;
using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace NINA.ObservatoryPlanner.Core {

    public enum OnStartAction { SlewAndCenter, SlewOnly, DoNotMove }
    public enum ConstraintBy { Altitude, Time }
    public enum ExposureOrder { RotateThroughFilters, FinishEachRowFirst }
    public enum RunMode { WithSafety, WithoutSafety }
    public enum GapMountAction { KeepTracking, StopTrackingAndPark, StopTrackingAndFindHome }
    /// <summary>What happens when it turns unsafe during the night (with a safety monitor).</summary>
    public enum UnsafeAction {
        /// <summary>4 End: the full shutdown (warm the camera, power off).</summary>
        RunEnd,
        /// <summary>Stop guiding, park, close the dome; power, connections and camera cooling stay on until safe again.</summary>
        CloseUpAndWait
    }
    /// <summary>
    /// The image type of an exposure row. DarkFlat is no longer offered (NINA 3 saves dark flats as DARK); it is only kept so
    /// lists saved with it still load, and such rows become Dark.
    /// </summary>
    public enum ExposureType { Light, Dark, Bias, Flat, DarkFlat }

    public abstract class Observable : INotifyPropertyChanged {
        public event PropertyChangedEventHandler PropertyChanged;

        protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null) {
            if (Equals(field, value)) { return false; }
            field = value;
            Raise(name);
            return true;
        }

        protected void Raise([CallerMemberName] string name = null) {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>"Start at" / "End at" of a target: by altitude or by clock time (local time of day).</summary>
    [JsonObject(MemberSerialization.OptIn)]
    public class TimeConstraint : Observable {
        private bool enabled;
        private ConstraintBy by = ConstraintBy.Time; // as in SGP, a new constraint is time-locked
        private double altitude = 30;
        private TimeSpan time = new TimeSpan(22, 0, 0);

        [JsonProperty] public bool Enabled { get => enabled; set => Set(ref enabled, value); }
        [JsonProperty] public ConstraintBy By { get => by; set => Set(ref by, value); }
        [JsonProperty] public double Altitude { get => altitude; set => Set(ref altitude, value); }
        [JsonProperty] public TimeSpan Time { get => time; set => Set(ref time, value); }

        public TimeConstraint Clone() => new TimeConstraint { Enabled = Enabled, By = By, Altitude = Altitude, Time = Time };
    }

    /// <summary>One exposure row of a target (SGP "event").</summary>
    [JsonObject(MemberSerialization.OptIn)]
    public class PlannerExposure : Observable {
        private bool enabled = true;
        private string filter = "L";
        private double exposureTime = 120;
        private ExposureType type = ExposureType.Light;
        private string binning = "1x1";
        private int count = 10;
        private int done;
        private bool isActive;

        [JsonProperty] public Guid Id { get; set; } = Guid.NewGuid();
        [JsonProperty] public bool Enabled { get => enabled; set => Set(ref enabled, value); }
        [JsonProperty] public string Filter { get => filter; set => Set(ref filter, value); }
        /// <summary>Seconds</summary>
        [JsonProperty] public double ExposureTime { get => exposureTime; set => Set(ref exposureTime, Math.Max(0, value)); }
        // Gain is not set per row: frames use the camera's gain (NINA's camera settings or the driver default).
        // Lists saved before 1.1 have a "Gain" value, which is ignored when they are read.
        [JsonProperty] public ExposureType Type { get => type; set => Set(ref type, value == ExposureType.DarkFlat ? ExposureType.Dark : value); }
        [JsonProperty] public string Binning { get => binning; set => Set(ref binning, string.IsNullOrWhiteSpace(value) ? "1x1" : value); }
        [JsonProperty] public int Count { get => count; set { if (Set(ref count, Math.Max(0, value))) { Raise(nameof(Remaining)); } } }
        [JsonProperty] public int Done { get => done; set { if (Set(ref done, Math.Max(0, value))) { Raise(nameof(Remaining)); } } }

        public int Remaining => Math.Max(0, Count - Done);

        /// <summary>True while this row is the one being imaged (the ▶ in the exposure list). Not saved.</summary>
        public bool IsActive { get => isActive; set => Set(ref isActive, value); }

        public PlannerExposure Clone(bool keepProgress) => new PlannerExposure {
            Enabled = Enabled, Filter = Filter, ExposureTime = ExposureTime, Type = Type, Binning = Binning, Count = Count, Done = keepProgress ? Done : 0
        };
    }

    [JsonObject(MemberSerialization.OptIn)]
    public class PlannerTarget : Observable {
        private bool enabled = true;
        private string name = "New target";
        private double raHours;
        private double decDegrees;
        private OnStartAction onStart = OnStartAction.SlewAndCenter;
        private bool rotate;
        private double positionAngle;
        private ExposureOrder order = ExposureOrder.FinishEachRowFirst;
        private double delayFirst;
        private double delayBetween;
        private ObservableCollection<PlannerExposure> exposures;

        public PlannerTarget() {
            Exposures = new ObservableCollection<PlannerExposure>();
        }

        [JsonProperty] public Guid Id { get; set; } = Guid.NewGuid();
        [JsonProperty] public bool Enabled { get => enabled; set => Set(ref enabled, value); }
        [JsonProperty] public string Name { get => name; set => Set(ref name, value); }
        /// <summary>J2000 right ascension in hours</summary>
        [JsonProperty] public double RaHours { get => raHours; set => Set(ref raHours, ((value % 24) + 24) % 24); }
        /// <summary>J2000 declination in degrees</summary>
        [JsonProperty] public double DecDegrees { get => decDegrees; set => Set(ref decDegrees, Math.Max(-90, Math.Min(90, value))); }
        [JsonProperty] public OnStartAction OnStart { get => onStart; set => Set(ref onStart, value); }
        [JsonProperty] public bool Rotate { get => rotate; set => Set(ref rotate, value); }
        [JsonProperty] public double PositionAngle { get => positionAngle; set => Set(ref positionAngle, ((value % 360) + 360) % 360); }
        [JsonProperty] public TimeConstraint Start { get; set; } = new TimeConstraint();
        [JsonProperty] public TimeConstraint End { get; set; } = new TimeConstraint();
        [JsonProperty] public ExposureOrder Order { get => order; set => Set(ref order, value); }
        /// <summary>Seconds to wait before the first exposure each time the target starts or resumes</summary>
        [JsonProperty] public double DelayFirst { get => delayFirst; set => Set(ref delayFirst, Math.Max(0, value)); }
        /// <summary>Seconds to wait between exposures</summary>
        [JsonProperty] public double DelayBetween { get => delayBetween; set => Set(ref delayBetween, Math.Max(0, value)); }

        [JsonProperty]
        public ObservableCollection<PlannerExposure> Exposures {
            get => exposures;
            set {
                if (exposures != null) {
                    exposures.CollectionChanged -= ExposuresChanged;
                    foreach (var e in exposures) { e.PropertyChanged -= ExposureChanged; }
                }
                exposures = value ?? new ObservableCollection<PlannerExposure>();
                exposures.CollectionChanged += ExposuresChanged;
                foreach (var e in exposures) { e.PropertyChanged += ExposureChanged; }
                RaiseProgress();
            }
        }

        public int TotalFrames => Exposures.Where(e => e.Enabled).Sum(e => e.Count);
        public int DoneFrames => Exposures.Where(e => e.Enabled).Sum(e => Math.Min(e.Done, e.Count));
        public bool IsComplete => TotalFrames > 0 && DoneFrames >= TotalFrames;
        public double Percent => TotalFrames == 0 ? 0 : Math.Round(100.0 * DoneFrames / TotalFrames);
        /// <summary>Remaining imaging time in hours, including the delay between exposures.</summary>
        public double HoursRemaining => Exposures.Where(e => e.Enabled).Sum(e => e.Remaining * (e.ExposureTime + DelayBetween)) / 3600.0;

        private void ExposuresChanged(object sender, NotifyCollectionChangedEventArgs e) {
            if (e.OldItems != null) { foreach (PlannerExposure x in e.OldItems) { x.PropertyChanged -= ExposureChanged; } }
            if (e.NewItems != null) { foreach (PlannerExposure x in e.NewItems) { x.PropertyChanged += ExposureChanged; } }
            RaiseProgress();
        }

        private void ExposureChanged(object sender, PropertyChangedEventArgs e) => RaiseProgress();

        private void RaiseProgress() {
            Raise(nameof(TotalFrames)); Raise(nameof(DoneFrames)); Raise(nameof(IsComplete)); Raise(nameof(Percent)); Raise(nameof(HoursRemaining));
        }

        public PlannerTarget Clone(bool keepProgress) => new PlannerTarget {
            Enabled = Enabled, Name = Name, RaHours = RaHours, DecDegrees = DecDegrees, OnStart = OnStart, Rotate = Rotate,
            PositionAngle = PositionAngle, Start = Start.Clone(), End = End.Clone(), Order = Order, DelayFirst = DelayFirst,
            DelayBetween = DelayBetween, Exposures = new ObservableCollection<PlannerExposure>(Exposures.Select(e => e.Clone(keepProgress)))
        };
    }

    [JsonObject(MemberSerialization.OptIn)]
    public class TargetList {
        [JsonProperty] public string Name { get; set; } = "Untitled";
        [JsonProperty] public ObservableCollection<PlannerTarget> Targets { get; set; } = new ObservableCollection<PlannerTarget>();
    }

    /// <summary>The ⚙ Options of the planner.</summary>
    [JsonObject(MemberSerialization.OptIn)]
    public class PlannerOptions : Observable {
        private RunMode runMode = RunMode.WithSafety;
        private int gapMinutes = 30;
        private GapMountAction gapMount = GapMountAction.StopTrackingAndPark;
        private bool gapCloseDome;
        private double defaultDelayFirst;
        private double defaultDelayBetween;
        private ExposureOrder defaultOrder = ExposureOrder.FinishEachRowFirst;
        private bool confirmDeleteTarget = true;
        private bool confirmDeleteExposure = true;
        private double darkSunAltitude = -12;
        private string workflowName;
        private int safeDelaySeconds;
        private bool autoStartOnLaunch;
        private bool autofocusAfterBegin = true;
        private bool guideLostWatch = true;
        private int guideLostWaitSeconds = 60;
        private GuideLostAction guideLostAction = GuideLostAction.StopForNight;
        private bool guidingCheck;
        private double guidingLimitPixels = 1.0;
        private int guidingCheckWaitSeconds = 120;
        private bool closeGuiderAppOnDisconnect = true;
        private bool closeMountAppOnDisconnect = true;
        private UnsafeAction unsafeAction = UnsafeAction.RunEnd;
        private double closeUpMaxHours = 2;

        [JsonProperty] public RunMode RunMode { get => runMode; set => Set(ref runMode, value); }
        /// <summary>Devices connected at Run and never disconnected by the planner (NINA device names).</summary>
        [JsonProperty] public ObservableCollection<string> KeepConnected { get; set; } = new ObservableCollection<string> { "Safety Monitor", "Switch", "Dome" };
        [JsonProperty] public int GapMinutes { get => gapMinutes; set => Set(ref gapMinutes, Math.Max(1, value)); }
        [JsonProperty] public GapMountAction GapMount { get => gapMount; set => Set(ref gapMount, value); }
        /// <summary>Only used with <see cref="GapMountAction.StopTrackingAndPark"/>.</summary>
        [JsonProperty] public bool GapCloseDome { get => gapCloseDome; set => Set(ref gapCloseDome, value); }
        [JsonProperty] public double DefaultDelayFirst { get => defaultDelayFirst; set => Set(ref defaultDelayFirst, Math.Max(0, value)); }
        [JsonProperty] public double DefaultDelayBetween { get => defaultDelayBetween; set => Set(ref defaultDelayBetween, Math.Max(0, value)); }
        [JsonProperty] public ExposureOrder DefaultOrder { get => defaultOrder; set => Set(ref defaultOrder, value); }
        [JsonProperty] public bool ConfirmDeleteTarget { get => confirmDeleteTarget; set => Set(ref confirmDeleteTarget, value); }
        [JsonProperty] public bool ConfirmDeleteExposure { get => confirmDeleteExposure; set => Set(ref confirmDeleteExposure, value); }
        /// <summary>Targets are only imaged while the Sun is below this altitude.</summary>
        [JsonProperty] public double DarkSunAltitude { get => darkSunAltitude; set => Set(ref darkSunAltitude, value); }

        /// <summary>With safety: once the monitor reports safe, wait this long (it must stay safe) before 1 Begin.</summary>
        [JsonProperty] public int SafeDelaySeconds { get => safeDelaySeconds; set => Set(ref safeDelaySeconds, Math.Max(0, value)); }
        /// <summary>Start Run forever / Run by itself when NINA starts (after the last workflow and target list are loaded).</summary>
        [JsonProperty] public bool AutoStartOnLaunch { get => autoStartOnLaunch; set => Set(ref autoStartOnLaunch, value); }
        /// <summary>After every 1 Begin (the equipment was off), autofocus before the first light frame.</summary>
        [JsonProperty] public bool AutofocusAfterBegin { get => autofocusAfterBegin; set => Set(ref autofocusAfterBegin, value); }
        /// <summary>Watch the guider during light frames and act when the guide star is lost.</summary>
        [JsonProperty] public bool GuideLostWatch { get => guideLostWatch; set => Set(ref guideLostWatch, value); }
        /// <summary>How long to wait for the guider to find the star again, counted from when it was lost.</summary>
        [JsonProperty] public int GuideLostWaitSeconds { get => guideLostWaitSeconds; set => Set(ref guideLostWaitSeconds, Math.Max(10, value)); }
        /// <summary>What happens when the star is not found again within the wait.</summary>
        [JsonProperty] public GuideLostAction GuideLostAction { get => guideLostAction; set => Set(ref guideLostAction, value); }
        /// <summary>Start light frames only while the guiding error is below the limit, and restart a frame when it stays above it.</summary>
        [JsonProperty] public bool GuidingCheck { get => guidingCheck; set => Set(ref guidingCheck, value); }
        /// <summary>The guiding error limit in guide camera pixels.</summary>
        [JsonProperty] public double GuidingLimitPixels { get => guidingLimitPixels; set => Set(ref guidingLimitPixels, Math.Max(0.1, value)); }
        /// <summary>How long to wait before a frame for the guiding error to come down; then the frame starts anyway.</summary>
        [JsonProperty] public int GuidingCheckWaitSeconds { get => guidingCheckWaitSeconds; set => Set(ref guidingCheckWaitSeconds, Math.Max(10, value)); }
        /// <summary>While NINA's sequence runs, disconnecting the guider also makes PHD2 disconnect its equipment and close.</summary>
        [JsonProperty] public bool CloseGuiderAppOnDisconnect { get => closeGuiderAppOnDisconnect; set => Set(ref closeGuiderAppOnDisconnect, value); }
        /// <summary>While NINA's sequence runs, disconnecting the mount also closes its ASCOM program (e.g. GS Server); PHD2 lets go of it first.</summary>
        [JsonProperty] public bool CloseMountAppOnDisconnect { get => closeMountAppOnDisconnect; set => Set(ref closeMountAppOnDisconnect, value); }
        /// <summary>With safety: what happens when it turns unsafe during the night, once 1 Begin has finished.</summary>
        [JsonProperty] public UnsafeAction UnsafeAction { get => unsafeAction; set => Set(ref unsafeAction, value); }
        /// <summary>Closed up for the weather: 4 End runs when it is still unsafe after this many hours, or when the night ends, whichever comes first.</summary>
        [JsonProperty] public double CloseUpMaxHours { get => closeUpMaxHours; set => Set(ref closeUpMaxHours, Math.Max(0.25, value)); }
        /// <summary>Name of the workflow last loaded or saved from the Equipment &amp; Safety tab.</summary>
        [JsonProperty] public string WorkflowName { get => workflowName; set => Set(ref workflowName, value); }
        /// <summary>Takes every setting from <paramref name="o"/> (switching NINA profile keeps this same object).</summary>
        public void CopyFrom(PlannerOptions o) {
            RunMode = o.RunMode; GapMinutes = o.GapMinutes; GapMount = o.GapMount; GapCloseDome = o.GapCloseDome;
            DefaultDelayFirst = o.DefaultDelayFirst; DefaultDelayBetween = o.DefaultDelayBetween; DefaultOrder = o.DefaultOrder;
            ConfirmDeleteTarget = o.ConfirmDeleteTarget; ConfirmDeleteExposure = o.ConfirmDeleteExposure; DarkSunAltitude = o.DarkSunAltitude;
            SafeDelaySeconds = o.SafeDelaySeconds; AutoStartOnLaunch = o.AutoStartOnLaunch; AutofocusAfterBegin = o.AutofocusAfterBegin;
            GuideLostWatch = o.GuideLostWatch; GuideLostWaitSeconds = o.GuideLostWaitSeconds; GuideLostAction = o.GuideLostAction;
            GuidingCheck = o.GuidingCheck; GuidingLimitPixels = o.GuidingLimitPixels; GuidingCheckWaitSeconds = o.GuidingCheckWaitSeconds;
            CloseGuiderAppOnDisconnect = o.CloseGuiderAppOnDisconnect; CloseMountAppOnDisconnect = o.CloseMountAppOnDisconnect;
            UnsafeAction = o.UnsafeAction; CloseUpMaxHours = o.CloseUpMaxHours; WorkflowName = o.WorkflowName;
            KeepConnected.Clear();
            foreach (var d in o.KeepConnected) { KeepConnected.Add(d); }
        }

        /// <summary>The safety monitor is always kept connected when running with safety.</summary>
        public bool Keeps(string device) => KeepConnected.Contains(device) || (RunMode == RunMode.WithSafety && device == "Safety Monitor");
    }
}
