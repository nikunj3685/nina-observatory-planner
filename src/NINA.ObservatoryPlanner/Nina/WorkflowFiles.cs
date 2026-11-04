using NINA.Core.Enum;
using NINA.Sequencer.Container;
using NINA.Sequencer.Trigger;
using NINA.Sequencer.Utility;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.ObservatoryPlanner.Nina {

    /// <summary>
    /// Saves and opens workflows (the Advanced Sequencer sequence holding the Observatory Planner block) in
    /// Root\Workflows as normal N.I.N.A. sequence files, so they also open in NINA's own sequencer.
    /// </summary>
    internal class WorkflowFiles {
        private readonly PlannerService planner;

        public WorkflowFiles(PlannerService planner) {
            this.planner = planner;
        }

        public string Folder => planner.Store.WorkflowsFolder;

        public string PathFor(string name) {
            var safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            return Path.Combine(Folder, safe + ".json");
        }

        public IReadOnlyList<FileInfo> List() =>
            Directory.Exists(Folder) ? new DirectoryInfo(Folder).GetFiles("*.json").OrderByDescending(f => f.LastWriteTime).ToList() : new List<FileInfo>();

        public ISequenceRootContainer CurrentRoot() =>
            (planner.ActiveContainer == null ? null : ItemUtility.GetRootContainer(planner.ActiveContainer.Parent))
            ?? (FindBlock(out _) is { } block ? ItemUtility.GetRootContainer(block.Parent) : null);

        public async Task Save(string name) {
            var root = CurrentRoot() ?? throw new InvalidOperationException("No Observatory Planner workflow is loaded in the Advanced Sequencer.");
            var json = Serialize(root);
            if (json != null) { Core.PlannerStore.WriteText(PathFor(name), json); lastSaved = json; } else { await planner.Nina.Sequence.SaveContainer(root, PathFor(name), CancellationToken.None); }
            if (json != null) { lastDisabled = WriteDisabled(PathFor(name), json, DisabledPaths(root)); }
            planner.Options.WorkflowName = name;
        }

        private string lastSaved;
        private string lastDisabled;

        // ---------- steps switched off in the Advanced Sequencer ----------
        // NINA's sequence file does not keep "disabled" (an instruction's status is not saved), so a restart would switch every step
        // back on. The planner keeps the list itself, next to the workflow file, and applies it when the workflow is opened.

        /// <summary>Where the disabled list of a workflow file is kept (not a .json, so it is not listed as a workflow).</summary>
        internal static string DisabledFileFor(string workflowPath) => workflowPath + ".disabled";

        /// <summary>Positions of the switched-off instructions and triggers: "i3" is the 4th item, "i3/i0" its first child, "i3/t1" its 2nd trigger.</summary>
        internal static List<string> DisabledPaths(ISequenceContainer c, string prefix = "") {
            var found = new List<string>();
            var items = c.GetItemsSnapshot().ToList();
            for (var k = 0; k < items.Count; k++) {
                var path = prefix + "i" + k;
                if (items[k].Status == SequenceEntityStatus.DISABLED) { found.Add(path); }
                if (items[k] is ISequenceContainer sub) { found.AddRange(DisabledPaths(sub, path + "/")); }
            }
            if (c is ITriggerable triggers) {
                var list = triggers.GetTriggersSnapshot().ToList();
                for (var k = 0; k < list.Count; k++) {
                    if (list[k].Status == SequenceEntityStatus.DISABLED) { found.Add(prefix + "t" + k); }
                }
            }
            return found;
        }

        /// <summary>Switches off the instructions and triggers at these positions.</summary>
        internal static void ApplyDisabled(ISequenceContainer c, ISet<string> paths, string prefix = "") {
            var items = c.GetItemsSnapshot().ToList();
            for (var k = 0; k < items.Count; k++) {
                var path = prefix + "i" + k;
                if (paths.Contains(path)) { items[k].Status = SequenceEntityStatus.DISABLED; }
                if (items[k] is ISequenceContainer sub) { ApplyDisabled(sub, paths, path + "/"); }
            }
            if (c is ITriggerable triggers) {
                var list = triggers.GetTriggersSnapshot().ToList();
                for (var k = 0; k < list.Count; k++) {
                    if (paths.Contains(prefix + "t" + k)) { list[k].Status = SequenceEntityStatus.DISABLED; }
                }
            }
        }

        internal static string HashOf(string json) =>
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json)))[..16];

        /// <summary>Writes the disabled list next to the workflow file (removes it when nothing is switched off). Returns the list as text.</summary>
        internal static string WriteDisabled(string workflowPath, string workflowJson, IReadOnlyList<string> paths) {
            var file = DisabledFileFor(workflowPath);
            var text = string.Join(",", paths);
            if (paths.Count == 0) {
                try { if (File.Exists(file)) { File.Delete(file); } } catch (IOException) { }
                return text;
            }
            // the hash ties the list to this exact workflow file: a file changed by anything else gets no stale list
            Core.PlannerStore.WriteText(file, Newtonsoft.Json.JsonConvert.SerializeObject(new { Hash = HashOf(workflowJson), Disabled = paths }, Newtonsoft.Json.Formatting.Indented));
            return text;
        }

        /// <summary>The saved disabled list of a workflow file, or an empty set when there is none or the workflow file has changed since.</summary>
        internal static HashSet<string> ReadDisabled(string workflowPath, string workflowJson) {
            var file = DisabledFileFor(workflowPath);
            try {
                if (!File.Exists(file)) { return new HashSet<string>(); }
                var saved = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(file));
                if ((string)saved["Hash"] != HashOf(workflowJson)) { return new HashSet<string>(); }
                return saved["Disabled"]?.Select(t => (string)t).Where(s => s != null).ToHashSet() ?? new HashSet<string>();
            } catch (Exception) { return new HashSet<string>(); }
        }

        /// <summary>The sequence as NINA's own sequence file, or null when NINA's converter can't be reached.</summary>
        public string Serialize(ISequenceRootContainer root) {
            var converter = FindConverter();
            var serialize = converter?.GetType().GetMethod("Serialize", new[] { typeof(ISequenceContainer) });
            return serialize?.Invoke(converter, new object[] { root }) as string;
        }

        /// <summary>
        /// Saves the open workflow when it has changed since the last save, so a NINA restart brings back the exact stages.
        /// A workflow without a name is saved as "Autosaved workflow". Returns true when it wrote the file.
        /// </summary>
        public bool AutoSave() {
            var root = CurrentRoot();
            if (root == null) { return false; }
            var json = Serialize(root);
            if (json == null) { return false; }
            var disabled = DisabledPaths(root);
            var disabledText = string.Join(",", disabled);
            if (json == lastSaved && disabledText == lastDisabled) { return false; }
            var name = planner.Options.WorkflowName ?? "Autosaved workflow";
            var path = PathFor(name);
            var sameFile = json == lastSaved || (lastSaved == null && File.Exists(path) && File.ReadAllText(path) == json);
            if (!sameFile) { Core.PlannerStore.WriteText(path, json); }
            var wrote = !sameFile || disabledText != lastDisabled;
            lastDisabled = WriteDisabled(path, json, disabled);
            lastSaved = json;
            if (planner.Options.WorkflowName == null) { planner.Options.WorkflowName = name; }
            return wrote;
        }

        /// <summary>Forget what was saved last (after switching profile).</summary>
        public void Reset() { lastSaved = null; lastDisabled = null; }

        /// <summary>
        /// Opens a sequence file into the Advanced Sequencer with NINA's own sequence converter. NINA 3.2 does not
        /// expose it to plugins, so it is reached through the sequencer view model; returns false if that changes.
        /// </summary>
        public bool Open(string path) {
            var converter = FindConverter();
            if (converter == null) { return false; }
            var deserialize = converter.GetType().GetMethod("Deserialize", new[] { typeof(string) });
            var container = deserialize?.Invoke(converter, new object[] { File.ReadAllText(path) });
            if (container is not ISequenceRootContainer root) {
                throw new InvalidDataException("The file is not a complete N.I.N.A. sequence.");
            }
            var text = File.ReadAllText(path);
            var disabled = ReadDisabled(path, text);
            if (disabled.Count > 0) { ApplyDisabled(root, disabled); }
            planner.Nina.Sequence.SetAdvancedSequence(root);
            planner.Options.WorkflowName = Path.GetFileNameWithoutExtension(path);
            lastSaved = text;
            lastDisabled = string.Join(",", DisabledPaths(root));
            if (disabled.Count > 0) { planner.Log.Info($"Workflow opened: {disabled.Count} step{(disabled.Count == 1 ? "" : "s")} stay switched off, as saved"); }
            return true;
        }

        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private object Sequence2VM() {
            var mediator = planner.Nina?.Sequence;
            if (mediator == null) { return null; }
            var navigation = mediator.GetType().GetField("sequenceNavigation", Any)?.GetValue(mediator);
            return navigation?.GetType().GetProperty("Sequence2VM", Any)?.GetValue(navigation);
        }

        private object FindConverter() {
            var sequence2 = Sequence2VM();
            return sequence2?.GetType().GetProperty("SequenceJsonConverter", Any)?.GetValue(sequence2);
        }

        /// <summary>The sequence open in NINA's Advanced Sequencer, or null when it can't be reached.</summary>
        public ISequenceRootContainer OpenSequence() {
            try {
                var sequence2 = Sequence2VM();
                var sequencer = sequence2?.GetType().GetProperty("Sequencer", Any)?.GetValue(sequence2);
                return sequencer?.GetType().GetProperty("MainContainer", Any)?.GetValue(sequencer) as ISequenceRootContainer;
            } catch (Exception) { return null; }
        }

        /// <summary>
        /// The Observatory Planner block in the sequence that is open now. Found again on every refresh, so the panel
        /// follows the sequence after it is replaced, reopened or edited in the Advanced Sequencer.
        /// </summary>
        public ObservatoryPlannerContainer FindBlock(out bool sequenceKnown) {
            var root = OpenSequence();
            sequenceKnown = root != null;
            return root == null ? null : Find(root);
        }

        private static ObservatoryPlannerContainer Find(ISequenceContainer c) {
            foreach (var item in c.Items) {
                if (item is ObservatoryPlannerContainer p) { return p; }
                if (item is ISequenceContainer inner && Find(inner) is { } found) { return found; }
            }
            return null;
        }
    }
}
