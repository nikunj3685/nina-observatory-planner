using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;

namespace NINA.ObservatoryPlanner.UI.Dialogs {

    public sealed record LibraryEntry(string Path, string Name, string Meta, bool IsCurrent);

    /// <summary>Open dialog for saved target lists or workflows: newest first, with Open and Delete.</summary>
    public partial class LibraryWindow : Window {
        private readonly string folder;
        private readonly Func<string, string> describe;
        private readonly string current;
        private readonly string what;

        public LibraryWindow(string title, string what, string folder, string current, Func<string, string> describe) {
            InitializeComponent();
            Title = title;
            this.what = what;
            this.folder = folder;
            this.current = current;
            this.describe = describe;
            Owner = Application.Current?.MainWindow is { IsLoaded: true } main ? main : null;
            if (Owner == null) { WindowStartupLocation = WindowStartupLocation.CenterScreen; }
            Fill();
        }

        /// <summary>The file to open, when the dialog closes with OK.</summary>
        public string Chosen { get; private set; }

        private void Fill() {
            var files = Directory.Exists(folder)
                ? new DirectoryInfo(folder).GetFiles("*.json").OrderByDescending(f => f.LastWriteTime).ToList()
                : new List<FileInfo>();
            list.ItemsSource = files.Select(f => new LibraryEntry(f.FullName, System.IO.Path.GetFileNameWithoutExtension(f.Name),
                $"Saved {f.LastWriteTime:yyyy-MM-dd HH:mm}{Describe(f.FullName)}",
                string.Equals(f.FullName, current, StringComparison.OrdinalIgnoreCase))).ToList();
            empty.Text = files.Count == 0 ? $"No saved {what} yet. Use Save as… to keep the current one." : $"Saved {what} in {folder}";
        }

        private string Describe(string path) {
            try { var d = describe?.Invoke(path); return string.IsNullOrEmpty(d) ? "" : " · " + d; } catch (Exception) { return ""; }
        }

        private void Open_Click(object sender, RoutedEventArgs e) {
            Chosen = ((LibraryEntry)((FrameworkElement)sender).DataContext).Path;
            DialogResult = true;
        }

        private void Delete_Click(object sender, RoutedEventArgs e) {
            var entry = (LibraryEntry)((FrameworkElement)sender).DataContext;
            if (entry.IsCurrent) {
                Ask.Info("Delete", $"\"{entry.Name}\" is open now. Open another one first.");
                return;
            }
            var (yes, _) = Ask.Confirm("Delete", $"Delete \"{entry.Name}\"? The file is removed from {folder}.", offerDontAsk: false);
            if (!yes) { return; }
            try { File.Delete(entry.Path); } catch (Exception ex) { Ask.Info("Delete", $"Could not delete the file: {ex.Message}"); }
            Fill();
        }

        private void Browse_Click(object sender, RoutedEventArgs e) {
            var dlg = new OpenFileDialog { InitialDirectory = folder, Filter = "JSON files (*.json)|*.json" };
            if (dlg.ShowDialog(this) != true) { return; }
            Chosen = dlg.FileName;
            DialogResult = true;
        }
    }
}
