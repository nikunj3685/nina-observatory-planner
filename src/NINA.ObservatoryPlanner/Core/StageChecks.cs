using System.Collections.Generic;
using System.Linq;

namespace NINA.ObservatoryPlanner.Core {

    public enum StageKind { Begin = 1, TargetStart = 2, Triggers = 3, End = 4 }

    /// <summary>What the checks need to know about one instruction in a stage.</summary>
    public sealed record StageEntry(string TypeName, string Name, string Device = null, string Category = null, string Summary = null) {
        public bool NeedsEquipment => EquipmentTypes.Contains(TypeName);
        public bool IsConnect => TypeName is "ConnectAllEquipment" or "ConnectEquipment";
        public bool IsDisconnect => TypeName is "DisconnectAllEquipment" or "DisconnectEquipment";

        // Instructions that need devices the planner connects in 1 Begin and disconnects in 4 End.
        private static readonly HashSet<string> EquipmentTypes = new() {
            "CoolCamera", "WarmCamera", "DewHeater", "SetReadoutMode", "SetUSBLimit", "TakeExposure", "TakeManyExposures", "SmartExposure",
            "TakeSubframeExposure", "SwitchFilter", "RunAutofocus", "MoveFocuserAbsolute", "MoveFocuserRelative", "MoveFocuserByTemperature",
            "MoveRotatorMechanical", "SolveAndRotate", "UnparkScope", "ParkScope", "FindHome", "SetTracking", "SlewScopeToAltAz", "SlewScopeToRaDec",
            "Center", "CenterAndRotate", "SolveAndSync", "StartGuiding", "StopGuiding", "Dither", "TrainedFlatExposure", "TrainedDarkFlatExposure",
            "AutoExposureFlat", "AutoBrightnessFlat", "SkyFlat"
        };
    }

    /// <summary>Order checks for the four stages. Plain-language warnings, never a block.</summary>
    public static class StageChecks {
        private static string Label(string device) => device == "Switch" ? "Switch hub" : device;

        public static IReadOnlyList<string> Check(StageKind stage, IReadOnlyList<StageEntry> items, PlannerOptions options) {
            var warnings = new List<string>();
            var kept = new[] { "Safety Monitor", "Switch", "Dome", "Weather", "Camera", "Filter Wheel", "Focuser", "Rotator", "Mount", "Guider", "Flat Panel" }
                .Where(options.Keeps).ToList();

            var dropped = items.Where(i => i.TypeName == "DisconnectEquipment" && i.Device != null && options.Keeps(i.Device))
                .Select(i => i.Device).Distinct().ToList();
            if (dropped.Count > 0) {
                warnings.Add($"{string.Join(", ", dropped.Select(Label))} {(dropped.Count > 1 ? "are" : "is")} set to stay connected (⚙ Options › Keep connected), but this stage disconnects {(dropped.Count > 1 ? "them" : "it")}. Remove the step or change the setting.");
            }

            var disconnectAll = items.ToList().FindIndex(i => i.TypeName == "DisconnectAllEquipment");
            if (disconnectAll >= 0 && kept.Count > 0) {
                warnings.Add($"\"Disconnect All Equipment\" also disconnects the devices you keep connected ({string.Join(", ", kept.Select(Label))}). Use \"Disconnect Equipment\" for each device instead.");
            } else if (disconnectAll >= 0 && items.Skip(disconnectAll + 1).Any(i => i.TypeName == "SetSwitchValue")) {
                warnings.Add("\"Disconnect All Equipment\" also disconnects the switch hub, so the \"Set Switch Value\" steps after it will fail. Move them above it.");
            }

            if (stage == StageKind.Begin) {
                var firstConnect = items.ToList().FindIndex(i => i.IsConnect);
                var early = items.Select((i, k) => (i, k)).FirstOrDefault(x => x.i.NeedsEquipment && (firstConnect < 0 || x.k < firstConnect)).i;
                if (early != null) {
                    warnings.Add(firstConnect < 0
                        ? $"\"{early.Name}\" needs equipment, but nothing in this stage connects it. Add \"Connect All Equipment\"."
                        : $"\"{early.Name}\" runs before equipment is connected. Move it below \"{items[firstConnect].Name}\".");
                }
                if (!items.Any(i => i.TypeName == "UnparkScope")) {
                    warnings.Add("Nothing in this stage unparks the mount. 4 End parks it, and some mount programs (e.g. GS Server) start parked: a parked mount can't slew. Add \"Unpark Scope\" at the end of 1 Begin.");
                }
                if (!options.Keeps("Switch")) {
                    var hub = items.ToList().FindIndex(i => i.TypeName == "ConnectAllEquipment" || (i.TypeName == "ConnectEquipment" && i.Device == "Switch"));
                    var firstSet = items.ToList().FindIndex(i => i.TypeName == "SetSwitchValue");
                    if (firstSet >= 0 && (hub < 0 || firstSet < hub)) {
                        warnings.Add("\"Set Switch Value\" runs before the switch hub is connected, so it fails. Add \"Connect Equipment\" (Switch) above it, or keep the switch hub connected (⚙ Options › Keep connected).");
                    }
                }
            }
            if (stage == StageKind.Triggers && items.Any(i => i.TypeName == "AutofocusAfterFilterChange")) {
                warnings.Add(items.Any(i => i.TypeName == PlannerAfTrigger)
                    ? "NINA's \"AF After Filter Change\" and \"AF after filter change (planner)\" are both here, so a filter change can focus twice. Remove NINA's."
                    : "NINA's \"AF After Filter Change\" compares with the filter of the last autofocus, and can miss the first filter change after a new target, a pause or a weather stop. Use \"AF after filter change (planner)\" instead.");
            }
            if (stage == StageKind.End) {
                var firstDisconnect = items.ToList().FindIndex(i => i.IsDisconnect);
                var late = firstDisconnect < 0 ? null : items.Skip(firstDisconnect + 1).FirstOrDefault(i => i.NeedsEquipment);
                if (late != null) {
                    warnings.Add($"\"{late.Name}\" runs after equipment starts disconnecting. Move it above \"{items[firstDisconnect].Name}\".");
                }
            }
            return warnings;
        }

        /// <summary>Type name of the planner's own filter-change autofocus trigger.</summary>
        public const string PlannerAfTrigger = "PlannerAutofocusOnFilterChange";

        /// <summary>Rotating filters with an autofocus-after-filter-change trigger focuses before almost every frame.</summary>
        public static string RotateWithFilterAf(IEnumerable<PlannerTarget> targets, IReadOnlyList<StageEntry> triggers) {
            if (!triggers.Any(t => t.TypeName is "AutofocusAfterFilterChange" or PlannerAfTrigger)) { return null; }
            var affected = targets.Where(t => t.Enabled && t.Order == ExposureOrder.RotateThroughFilters
                && t.Exposures.Where(e => e.Enabled).Select(e => e.Filter).Distinct().Count() > 1).Select(t => t.Name).ToList();
            if (affected.Count == 0) { return null; }
            return $"Autofocus after filter change will run autofocus before almost every frame for targets that rotate through filters: {string.Join(", ", affected)}. Use \"Finish each row first\", or replace it with AF After Temperature Change or AF After HFR Increase.";
        }
    }
}
