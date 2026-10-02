using NINA.Sequencer.Container;
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
            planner.Options.WorkflowName = name;
        }

        private string lastSaved;

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
            if (json == null || json == lastSaved) { return false; }
            var name = planner.Options.WorkflowName ?? "Autosaved workflow";
            var path = PathFor(name);
            if (lastSaved == null && File.Exists(path) && File.ReadAllText(path) == json) { lastSaved = json; return false; }
            Core.PlannerStore.WriteText(path, json);
            lastSaved = json;
            if (planner.Options.WorkflowName == null) { planner.Options.WorkflowName = name; }
            return true;
        }

        /// <summary>Forget what was saved last (after switching profile).</summary>
        public void Reset() => lastSaved = null;

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
            planner.Nina.Sequence.SetAdvancedSequence(root);
            planner.Options.WorkflowName = Path.GetFileNameWithoutExtension(path);
            lastSaved = File.ReadAllText(path);
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
