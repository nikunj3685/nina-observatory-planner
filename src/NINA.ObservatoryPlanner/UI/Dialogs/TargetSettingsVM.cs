using NINA.Astrometry;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.ObservatoryPlanner.Core;
using NINA.ObservatoryPlanner.Nina;
using NINA.Sequencer.SequenceItem.Platesolving;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace NINA.ObservatoryPlanner.UI.Dialogs {

    /// <summary>
    /// "Start at" / "End at" row of the Target Settings: driven by altitude or by clock time (the ° alt / 🕑 time
    /// button picks which). The other value is shown for tonight, calculated from the target's coordinates.
    /// </summary>
    public sealed class ConstraintRow : Observable {
        private readonly TargetSettingsVM owner;
        private readonly bool isStart;
        private string altitudeText;
        private string timeText;

        public ConstraintRow(TargetSettingsVM owner, TimeConstraint constraint, bool isStart) {
            this.owner = owner;
            this.isStart = isStart;
            Constraint = constraint.Clone();
            altitudeText = Constraint.Altitude.ToString("0.#", CultureInfo.CurrentCulture);
            timeText = Constraint.Time.ToString(@"hh\:mm");
            Toggle = new Command(() => { ByAltitude = !ByAltitude; });
        }

        public TimeConstraint Constraint { get; }
        public string Label => isStart ? "Start at" : "End at";
        public ICommand Toggle { get; }

        public bool Enabled { get => Constraint.Enabled; set { Constraint.Enabled = value; RaiseAll(); } }
        public bool ByAltitude { get => Constraint.By == ConstraintBy.Altitude; set { Constraint.By = value ? ConstraintBy.Altitude : ConstraintBy.Time; RaiseAll(); } }
        public bool AltitudeEditable => Enabled && ByAltitude;
        public bool TimeEditable => Enabled && !ByAltitude;
        public string ToggleText => ByAltitude ? "° alt" : "🕑 time";
        public string ToggleTip => ByAltitude ? "Uses altitude. Click to use clock time" : "Uses clock time. Click to use altitude";

        public string AltitudeText {
            get => ByAltitude ? altitudeText : Counterpart().Altitude;
            set {
                altitudeText = value;
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out var a)) { Constraint.Altitude = Math.Max(-90, Math.Min(90, a)); }
                RaiseAll();
            }
        }

        public string TimeText {
            get => ByAltitude ? Counterpart().Time : timeText;
            set {
                timeText = value;
                if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var t) && t >= TimeSpan.Zero && t < TimeSpan.FromDays(1)) { Constraint.Time = t; }
                RaiseAll();
            }
        }

        public string Note => !Enabled ? "" : ByAltitude ? (Counterpart().Time == "" ? "not reached" : "tonight") : "tonight";

        /// <summary>Sets the row to a clock time picked on the Planning tools chart.</summary>
        public void SetTime(DateTime local) {
            Constraint.Enabled = true;
            Constraint.By = ConstraintBy.Time;
            Constraint.Time = local.TimeOfDay;
            timeText = Constraint.Time.ToString(@"hh\:mm");
            if (owner.TryCoordinates(out var ra, out var dec)) {
                Constraint.Altitude = Math.Round(owner.Night.Altitude(ra, dec, local) * 10) / 10;
                altitudeText = Constraint.Altitude.ToString("0.#", CultureInfo.CurrentCulture);
            }
            RaiseAll();
        }

        private (string Altitude, string Time) Counterpart() {
            if (!owner.TryCoordinates(out var ra, out var dec)) { return ("", ""); }
            if (ByAltitude) {
                var at = owner.Night.TimeAtAltitude(ra, dec, Constraint.Altitude, rising: isStart);
                return (altitudeText, at?.ToString("HH:mm") ?? "");
            }
            var when = NightTime.At(Constraint.Time, owner.Night.Now);
            return (owner.Night.Altitude(ra, dec, when).ToString("0.#", CultureInfo.CurrentCulture), timeText);
        }

        public void RaiseAll() {
            Raise(nameof(Enabled)); Raise(nameof(ByAltitude)); Raise(nameof(AltitudeEditable)); Raise(nameof(TimeEditable));
            Raise(nameof(ToggleText)); Raise(nameof(ToggleTip)); Raise(nameof(AltitudeText)); Raise(nameof(TimeText)); Raise(nameof(Note));
        }
    }

    /// <summary>The ⚙ Target Settings popup: works on a copy and writes it back on OK.</summary>
    public sealed class TargetSettingsVM : Observable {
        private readonly PlannerService planner;
        private readonly PlannerTarget target;
        private string name;
        private string raText;
        private string decText;
        private OnStartAction onStart;
        private bool rotate;
        private string angleText;
        private string status;
        private bool busy;

        public TargetSettingsVM(PlannerService planner, PlannerTarget target, bool isNew, NightPlan night = null) {
            this.planner = planner;
            this.target = target;
            IsNew = isNew;
            Night = night ?? NightPlan.For(planner.Site, DateTime.Now, l => l.ToUniversalTime());
            name = target.Name;
            raText = CoordinateText.FormatRa(target.RaHours);
            decText = CoordinateText.FormatDec(target.DecDegrees);
            onStart = target.OnStart;
            rotate = target.Rotate;
            angleText = target.PositionAngle.ToString("0.#", CultureInfo.CurrentCulture);
            Start = new ConstraintRow(this, target.Start, isStart: true);
            End = new ConstraintRow(this, target.End, isStart: false);

            SlewNowCommand = new Command(() => _ = SlewNow(center: false), _ => !busy);
            CenterNowCommand = new Command(() => _ = SlewNow(center: true), _ => !busy);
            FromFramingCommand = new Command(FromFraming);
            FromPlanetariumCommand = new Command(() => _ = FromPlanetarium(), _ => !busy);
        }

        public bool IsNew { get; }
        public string Title => IsNew ? "New Target" : "Target Settings";
        public NightPlan Night { get; }
        public Site Site => planner.Site;
        public ConstraintRow Start { get; }
        public ConstraintRow End { get; }

        public string Name { get => name; set { name = value; Raise(); } }
        public string RaText { get => raText; set { raText = value; Raise(); CoordinatesChanged(); } }
        public string DecText { get => decText; set { decText = value; Raise(); CoordinatesChanged(); } }
        public string CoordinateError => TryCoordinates(out _, out _) ? null : "Enter RA as 05h35m17s (or hours) and Dec as -05°23'28\" (or degrees).";

        public bool CenterOnStart { get => onStart == OnStartAction.SlewAndCenter; set { if (value) { onStart = OnStartAction.SlewAndCenter; RaiseStart(); } } }
        public bool SlewOnStart { get => onStart == OnStartAction.SlewOnly; set { if (value) { onStart = OnStartAction.SlewOnly; RaiseStart(); } } }
        public bool StayOnStart { get => onStart == OnStartAction.DoNotMove; set { if (value) { onStart = OnStartAction.DoNotMove; RaiseStart(); } } }

        public bool Rotate { get => rotate; set { rotate = value; Raise(); Raise(nameof(RotateHint)); } }
        public string AngleText { get => angleText; set { angleText = value; Raise(); Raise(nameof(RotateHint)); } }
        public string RotateHint {
            get {
                if (!Rotate) { return "Camera rotation is left as it is."; }
                if (!double.TryParse(AngleText, NumberStyles.Float, CultureInfo.CurrentCulture, out var a)) { return "Enter the position angle in degrees."; }
                a = ((a % 360) + 360) % 360;
                return $"Camera will rotate to {a:0.#}°. NINA also accepts {(a + 180) % 360:0.#}° (the same framing turned over), within the rotation tolerance of your profile.";
            }
        }

        public string Status { get => status; private set { status = value; Raise(); } }

        public ICommand SlewNowCommand { get; }
        public ICommand CenterNowCommand { get; }
        public ICommand FromFramingCommand { get; }
        public ICommand FromPlanetariumCommand { get; }

        public bool TryCoordinates(out double raHours, out double decDeg) {
            decDeg = 0;
            return CoordinateText.TryParseRa(RaText, out raHours) & CoordinateText.TryParseDec(DecText, out decDeg);
        }

        private void CoordinatesChanged() {
            Raise(nameof(CoordinateError));
            Start.RaiseAll();
            End.RaiseAll();
            CoordinatesUpdated?.Invoke();
        }

        /// <summary>Raised when the chart has to be redrawn.</summary>
        public event Action CoordinatesUpdated;

        private void RaiseStart() { Raise(nameof(CenterOnStart)); Raise(nameof(SlewOnStart)); Raise(nameof(StayOnStart)); }

        /// <summary>Validates and writes the edits to the target. Returns an error to show, or null.</summary>
        public string Apply() {
            if (string.IsNullOrWhiteSpace(Name)) { return "Give the target a name."; }
            if (!TryCoordinates(out var ra, out var dec)) { return CoordinateError; }
            if (!double.TryParse(AngleText, NumberStyles.Float, CultureInfo.CurrentCulture, out var angle)) { return "Enter the rotation angle in degrees."; }
            target.Name = Name.Trim();
            target.RaHours = ra;
            target.DecDegrees = dec;
            target.OnStart = onStart;
            target.Rotate = Rotate;
            target.PositionAngle = angle;
            Copy(Start.Constraint, target.Start);
            Copy(End.Constraint, target.End);
            return null;
        }

        private static void Copy(TimeConstraint from, TimeConstraint to) {
            to.Enabled = from.Enabled;
            to.By = from.By;
            to.Altitude = from.Altitude;
            to.Time = from.Time;
        }

        /// <summary>The target as edited so far, for the Planning tools chart.</summary>
        public PlannerTarget Preview() {
            var t = target.Clone(keepProgress: true);
            if (TryCoordinates(out var ra, out var dec)) { t.RaHours = ra; t.DecDegrees = dec; }
            t.Name = Name;
            t.Start = Start.Constraint.Clone();
            t.End = End.Constraint.Clone();
            return t;
        }

        private void SetCoordinates(Coordinates c, string newName, double? positionAngle) {
            var j2000 = c.Transform(Epoch.J2000);
            RaText = CoordinateText.FormatRa(j2000.RA);
            DecText = CoordinateText.FormatDec(j2000.Dec);
            if (!string.IsNullOrWhiteSpace(newName)) { Name = newName; }
            if (positionAngle is double pa) {
                AngleText = pa.ToString("0.#", CultureInfo.CurrentCulture);
                Rotate = Math.Abs(pa) > 0.01;
            }
        }

        private void FromFraming() {
            var framing = planner.Nina.FramingAssistant;
            var rect = framing?.Rectangle;
            if (framing == null || !framing.RectangleCalculated || rect == null) {
                Status = "Frame a target in NINA's Framing tab first.";
                return;
            }
            SetCoordinates(rect.Coordinates, framing.DSO?.Name, rect.DSOPositionAngle);
            Status = $"Filled in from the Framing Assistant: {Name}";
        }

        private async Task FromPlanetarium() {
            busy = true;
            Status = "Asking the planetarium for its selected object…";
            try {
                var dso = await planner.Nina.PlanetariumFactory.GetPlanetarium().GetTarget();
                if (dso == null) { Status = "The planetarium did not return an object. Select one there first."; return; }
                SetCoordinates(dso.Coordinates, dso.Name, null);
                Status = $"Filled in from the planetarium: {Name}";
            } catch (Exception ex) {
                Logger.Error(ex);
                Status = $"Could not get the target from the planetarium: {ex.Message}";
            } finally {
                busy = false;
                CommandManager.InvalidateRequerySuggested();
            }
        }

        /// <summary>Below this altitude a slew is reported as a possible collision (pier, tripod, dome wall, ground).</summary>
        public const double LowAltitude = 10;

        /// <summary>Asks before a risky slew: (title, message) → go ahead. A dialog by default; tests replace it.</summary>
        public Func<string, string, bool> Confirm { get; set; } = (title, message) =>
            Ask.Choose(title, message, Ask.Danger("Slew anyway", "yes"), Ask.Cancel()) == "yes";

        /// <summary>The collision warning for a slew to these coordinates now, or null when the target is high enough.</summary>
        public string CollisionWarning(double raHours, double decDeg, DateTime utcNow) {
            var alt = Sky.Altitude(raHours, decDeg, planner.Site, DateTime.SpecifyKind(utcNow, DateTimeKind.Utc));
            if (alt < 0) {
                return $"{Name} is below the horizon now (altitude {alt:0.#}°). Slewing there points the telescope at the ground and can hit the pier, tripod or dome. Slew anyway?";
            }
            if (alt < LowAltitude) {
                return $"{Name} is very low now (altitude {alt:0.#}°, below {LowAltitude:0}°). The telescope or camera can hit the pier, tripod or dome wall. Slew anyway?";
            }
            return null;
        }

        /// <summary>Slew now / Center now. Returns what happened (also shown in the dialog).</summary>
        public async Task<string> SlewNowAsync(bool center) {
            await SlewNow(center);
            return Status;
        }

        private async Task SlewNow(bool center) {
            if (!TryCoordinates(out var ra, out var dec)) { Status = CoordinateError; return; }
            if (planner.Nina.Telescope.GetInfo()?.Connected != true) { Status = "Connect the mount first."; return; }
            var warning = CollisionWarning(ra, dec, DateTime.UtcNow);
            if (warning != null) {
                planner.Log.Info($"Collision warning before {(center ? "centering" : "slewing")}: {warning}");
                if (!Confirm(center ? "Center now: possible collision" : "Slew now: possible collision", warning)) {
                    Status = "Cancelled: the target is too low to slew to safely.";
                    planner.Log.Info("Slew cancelled after the collision warning");
                    return;
                }
                planner.Log.Info("Slew confirmed after the collision warning");
            }
            busy = true;
            var coordinates = new Coordinates(Angle.ByHours(ra), Angle.ByDegree(dec), Epoch.J2000);
            try {
                if (center) {
                    Status = "Slewing and centering…";
                    var item = (Center)planner.Factory.Center();
                    item.Coordinates = new InputCoordinates(coordinates);
                    await item.Run(new Progress<ApplicationStatus>(s => Status = s.Status), CancellationToken.None);
                    Status = item.Status == NINA.Core.Enum.SequenceEntityStatus.FINISHED ? "Centered." : "Centering did not finish. See NINA's notifications.";
                } else {
                    Status = "Slewing…";
                    Status = await planner.Nina.Telescope.SlewToCoordinatesAsync(coordinates, CancellationToken.None) ? "Slew finished." : "The slew did not finish.";
                }
            } catch (Exception ex) {
                Logger.Error(ex);
                Status = $"{(center ? "Centering" : "Slew")} failed: {ex.Message}";
            } finally {
                busy = false;
                CommandManager.InvalidateRequerySuggested();
                planner.Log.Info($"{(center ? "Center now" : "Slew now")} {Name}: {Status}");
            }
        }

        public IReadOnlyList<string> Problems() {
            var list = new List<string>();
            if (CoordinateError != null) { list.Add(CoordinateError); }
            return list;
        }
    }
}
