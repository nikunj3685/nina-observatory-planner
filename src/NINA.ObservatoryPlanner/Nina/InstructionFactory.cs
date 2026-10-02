using NINA.Astrometry;
using NINA.Core.Locale;
using NINA.Core.Utility;
using NINA.Core.Model.Equipment;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Model;
using NINA.ObservatoryPlanner.Core;
using NINA.Sequencer.Conditions;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.SequenceItem.Autofocus;
using NINA.Sequencer.SequenceItem.Camera;
using NINA.Sequencer.SequenceItem.Connect;
using NINA.Sequencer.SequenceItem.Dome;
using NINA.Sequencer.SequenceItem.FilterWheel;
using NINA.Sequencer.SequenceItem.Guider;
using NINA.Sequencer.SequenceItem.Imaging;
using NINA.Sequencer.SequenceItem.Platesolving;
using NINA.Sequencer.SequenceItem.Switch;
using NINA.Sequencer.SequenceItem.Telescope;
using NINA.Sequencer.SequenceItem.Utility;
using NINA.Sequencer.Trigger;
using NINA.Sequencer.Trigger.Autofocus;
using NINA.Sequencer.Trigger.Dome;
using NINA.Sequencer.Trigger.Guider;
using NINA.Sequencer.Trigger.MeridianFlip;
using System;
using System.Linq;
using System.Windows.Media;

namespace NINA.ObservatoryPlanner.Nina {

    /// <summary>
    /// Creates NINA's own instructions from code, with the name, icon and category the sequencer shows
    /// (the same values NINA's [ExportMetadata] gives them).
    /// </summary>
    public class InstructionFactory {
        private readonly NinaServices s;

        public InstructionFactory(NinaServices services) {
            s = services;
        }

        private static string L(string key) {
            try {
                var v = Loc.Instance[key];
                return string.IsNullOrEmpty(v) ? key : v;
            } catch (Exception) {
                return key;
            }
        }

        private static GeometryGroup Icon(string key) {
            try { return System.Windows.Application.Current?.TryFindResource(key) as GeometryGroup; } catch (Exception) { return null; }
        }

        private static T Item<T>(T item, string type, string icon, string category) where T : SequenceItem {
            item.Name = L($"Lbl_SequenceItem_{type}_Name");
            item.Description = L($"Lbl_SequenceItem_{type}_Description");
            item.Icon = Icon(icon);
            item.Category = L($"Lbl_SequenceCategory_{category}");
            return item;
        }

        private static T Trig<T>(T trigger, string nameKey, string icon, string category) where T : SequenceTrigger {
            trigger.Name = L($"Lbl_SequenceTrigger_{nameKey}_Name");
            trigger.Description = L($"Lbl_SequenceTrigger_{nameKey}_Description");
            trigger.Icon = Icon(icon);
            trigger.Category = L($"Lbl_SequenceCategory_{category}");
            return trigger;
        }

        // ---- Connect ----
        public ISequenceItem ConnectAll() => Item(new ConnectAllEquipment(s.Profile, s.Camera, s.FilterWheel, s.Focuser, s.Rotator, s.Telescope, s.Guider, s.Switches,
            s.FlatDevice, s.Weather, s.Dome, s.SafetyMonitor), "Connector_ConnectAllEquipment", "ConnectAllSVG", "Connect");

        public ISequenceItem Connect(string device) {
            var i = Item(new ConnectEquipment(s.Profile, s.Camera, s.FilterWheel, s.Focuser, s.Rotator, s.Telescope, s.Guider, s.Switches,
                s.FlatDevice, s.Weather, s.Dome, s.SafetyMonitor), "Connector_ConnectEquipment", "ConnectSVG", "Connect");
            i.SelectedDevice = device;
            return i;
        }

        // NINA 3.2 declares DisconnectEquipment as internal, so it is created by reflection with the same constructor NINA uses.
        private static readonly Type DisconnectType = typeof(ConnectEquipment).Assembly.GetType("NINA.Sequencer.SequenceItem.Connect.DisconnectEquipment");

        public ISequenceItem Disconnect(string device) {
            if (DisconnectType == null) { throw new InvalidOperationException("NINA's Disconnect Equipment instruction was not found"); }
            var i = (SequenceItem)Activator.CreateInstance(DisconnectType, s.Camera, s.FilterWheel, s.Focuser, s.Rotator, s.Telescope, s.Guider, s.Switches,
                s.FlatDevice, s.Weather, s.Dome, s.SafetyMonitor);
            Item(i, "Connector_DisconnectEquipment", "DisconnectSVG", "Connect");
            DisconnectType.GetProperty("SelectedDevice")?.SetValue(i, device);
            return i;
        }

        /// <summary>The device chosen in Connect Equipment / Disconnect Equipment, or null for other instructions.</summary>
        public static string DeviceOf(ISequenceItem item) {
            if (item is ConnectEquipment c) { return c.SelectedDevice; }
            if (item?.GetType() == DisconnectType) { return DisconnectType.GetProperty("SelectedDevice")?.GetValue(item) as string; }
            return null;
        }

        // ---- Switch ----
        public ISequenceItem SetSwitch(short index, double value) {
            var i = Item(new SetSwitchValue(s.Switches), "Switch_SetSwitchValue", "ButtonSVG", "Switch");
            i.SwitchIndex = index;
            i.Value = value;
            return i;
        }

        /// <summary>
        /// Set Switch Value for the writable switch called <paramref name="name"/>. NINA stores the switch by its position
        /// on the hub, so the name is looked up on the connected hub; when the hub is not connected or has no such switch,
        /// <paramref name="fallbackIndex"/> is used and the switch can be picked in the Advanced Sequencer.
        /// </summary>
        public ISequenceItem SetSwitch(string name, double value, short fallbackIndex) {
            var index = SwitchIndexOf(name);
            if (index < 0) {
                Logger.Warning($"Observatory Planner: switch \"{name}\" not found on the connected switch hub; using switch #{fallbackIndex}");
                index = fallbackIndex;
            }
            return SetSwitch(index, value);
        }

        /// <summary>Position of a writable switch by name (case and spaces ignored), or -1.</summary>
        public short SwitchIndexOf(string name) {
            try {
                var switches = s.Switches?.GetInfo()?.WritableSwitches;
                if (switches == null) { return -1; }
                static string Key(string n) => (n ?? "").Trim().ToUpperInvariant();
                for (short i = 0; i < switches.Count; i++) {
                    if (Key(switches[i].Name) == Key(name)) { return i; }
                }
            } catch (Exception ex) { Logger.Error(ex); }
            return -1;
        }

        // ---- Utility ----
        public ISequenceItem WaitSeconds(double seconds) {
            var i = Item(new WaitForTimeSpan(), "Utility_WaitForTimeSpan", "HourglassSVG", "Utility");
            i.Time = seconds;
            return i;
        }

        /// <summary>Wait for Time using one of NINA's time providers, e.g. "DuskProvider" (astronomical dusk).</summary>
        public ISequenceItem WaitForTime(string providerTypeName) {
            var i = Item(new WaitForTime(s.DateTimeProviders), "Utility_WaitForTime", "ClockSVG", "Utility");
            var provider = s.DateTimeProviders?.FirstOrDefault(p => p.GetType().Name == providerTypeName);
            if (provider != null) { i.SelectedProvider = provider; }
            return i;
        }

        // ---- Camera ----
        public ISequenceItem CoolCamera(double temperature, double minutes) {
            var i = Item(new CoolCamera(s.Camera), "Camera_CoolCamera", "SnowflakeSVG", "Camera");
            i.Temperature = temperature;
            i.Duration = minutes;
            return i;
        }

        public ISequenceItem WarmCamera(double minutes) {
            var i = Item(new WarmCamera(s.Camera), "Camera_WarmCamera", "Fire_NoFill_SVG", "Camera");
            i.Duration = minutes;
            return i;
        }

        public TakeExposure TakeExposure(PlannerExposure e) {
            var i = Item(new TakeExposure(s.Profile, s.Camera, s.Imaging, s.ImageSave, s.ImageHistory), "Imaging_TakeExposure", "CameraSVG", "Camera");
            i.ExposureTime = e.ExposureTime;
            i.Gain = e.Gain;
            i.ImageType = CaptureSequence.ImageTypes.LIGHT;
            i.Binning = ParseBinning(e.Binning);
            return i;
        }

        public static BinningMode ParseBinning(string text) {
            var parts = (text ?? "1x1").ToLowerInvariant().Split('x');
            short x = 1, y = 1;
            if (parts.Length == 2 && short.TryParse(parts[0], out var px) && short.TryParse(parts[1], out var py)) { x = px; y = py; }
            return new BinningMode(x, y);
        }

        /// <summary>Switch Filter to the profile's filter with this name. Null when the profile has no such filter.</summary>
        public SwitchFilter SwitchFilter(string filterName) {
            var filter = s.Profile.ActiveProfile?.FilterWheelSettings?.FilterWheelFilters?
                .FirstOrDefault(f => string.Equals(f.Name, filterName, StringComparison.OrdinalIgnoreCase));
            if (filter == null) { return null; }
            var i = Item(new SwitchFilter(s.Profile, s.FilterWheel), "FilterWheel_SwitchFilter", "FW_NoFill_SVG", "FilterWheel");
            i.Filter = filter;
            return i;
        }

        // ---- Dome ----
        public ISequenceItem OpenDome() => Item(new OpenDomeShutter(s.Dome), "Dome_OpenDomeShutter", "ObservatorySVG", "Dome");
        public ISequenceItem CloseDome() => Item(new CloseDomeShutter(s.Dome), "Dome_CloseDomeShutter", "ObservatoryClosedSVG", "Dome");
        public ISequenceItem EnableDomeSync() => Item(new EnableDomeSynchronization(s.Dome, s.Telescope), "Dome_EnableDomeSynchronization", "LoopSVG", "Dome");
        public ISequenceItem DisableDomeSync() => Item(new DisableDomeSynchronization(s.Dome, s.Telescope), "Dome_DisableDomeSynchronization", "CancelSVG", "Dome");

        // ---- Telescope ----
        public ISequenceItem Unpark() => Item(new UnparkScope(s.Telescope), "Telescope_UnparkScope", "UnparkSVG", "Telescope");
        public ISequenceItem Park() => Item(new ParkScope(s.Telescope, s.Guider), "Telescope_ParkScope", "ParkSVG", "Telescope");
        public ISequenceItem FindHome() => Item(new FindHome(s.Telescope, s.Guider), "Telescope_FindHome", "HomeSVG", "Telescope");

        public ISequenceItem SetTracking(TrackingMode mode) {
            var i = Item(new SetTracking(s.Telescope), "Telescope_SetTracking", "SpeedometerSVG", "Telescope");
            i.TrackingMode = mode;
            return i;
        }

        public ISequenceItem SlewToTarget() => Item(new SlewScopeToRaDec(s.Telescope, s.Guider), "Telescope_SlewScopeToRaDec", "SlewToRaDecSVG", "Telescope");

        public ISequenceItem Center() => Item(new Center(s.Profile, s.Telescope, s.Imaging, s.FilterWheel, s.Guider, s.Dome, s.DomeFollower,
            s.PlateSolverFactory, s.WindowServiceFactory), "Platesolving_Center", "PlatesolveSVG", "Telescope");

        public ISequenceItem CenterAndRotate(double positionAngle) {
            var i = Item(new CenterAndRotate(s.Profile, s.Telescope, s.Imaging, s.Rotator, s.FilterWheel, s.Guider, s.Dome, s.DomeFollower,
                s.PlateSolverFactory, s.WindowServiceFactory), "Platesolving_CenterAndRotate", "PlatesolveAndRotateSVG", "Telescope");
            i.PositionAngle = positionAngle;
            return i;
        }

        // ---- Guider ----
        public ISequenceItem StartGuiding(bool forceCalibration) {
            var i = Item(new StartGuiding(s.Guider), "Guider_StartGuiding", "GuiderSVG", "Guider");
            i.ForceCalibration = forceCalibration;
            return i;
        }

        public ISequenceItem StopGuiding() => Item(new StopGuiding(s.Guider), "Guider_StopGuiding", "StopGuiderSVG", "Guider");

        // ---- Triggers ----
        public ISequenceTrigger DitherAfter(int exposures) {
            var t = Trig(new DitherAfterExposures(s.Guider, s.ImageHistory, s.Profile), "Guider_DitherAfterExposures", "DitherSVG", "Guider");
            t.AfterExposures = exposures;
            return t;
        }

        public ISequenceItem RunAutofocus() => Item(new RunAutofocus(s.Profile, s.ImageHistory, s.Camera, s.FilterWheel, s.Focuser, s.AutoFocusVMFactory),
            "Autofocus_RunAutofocus", "AutoFocusSVG", "Focuser");

        public ISequenceTrigger AutofocusAfterFilterChange() => Trig(new AutofocusAfterFilterChange(s.Profile, s.ImageHistory, s.Camera, s.FilterWheel,
            s.Focuser, s.AutoFocusVMFactory), "AutofocusAfterFilterChangeTrigger", "AutoFocusAfterFilterSVG", "Focuser");

        public ISequenceTrigger MeridianFlip() => Trig(new MeridianFlipTrigger(s.Profile, s.Camera, s.Telescope, s.Focuser, s.ApplicationStatus,
            s.MeridianFlipVMFactory), "MeridianFlipTrigger", "MeridianFlipSVG", "Telescope");

        public ISequenceTrigger SynchronizeDome() => Trig(new SynchronizeDomeTrigger(s.Profile, s.Telescope, s.Dome, s.DomeFollower, s.ApplicationStatus),
            "SynchronizeDomeTrigger", "LoopSVG", "Dome");

        // ---- Containers ----
        public DeepSkyObjectContainer TargetContainer(PlannerTarget t) {
            var c = new DeepSkyObjectContainer(s.Profile, s.NighttimeCalculator, s.FramingAssistant, s.Application, s.PlanetariumFactory, s.Camera, s.FilterWheel) {
                Name = t.Name,
                Icon = Icon("TelescopeSVG"),
                Category = L("Lbl_SequenceCategory_Container")
            };
            c.Target.TargetName = t.Name;
            c.Target.InputCoordinates = new InputCoordinates(new Coordinates(Angle.ByHours(t.RaHours), Angle.ByDegree(t.DecDegrees), Epoch.J2000));
            c.Target.PositionAngle = t.PositionAngle;
            return c;
        }
    }
}
