using Moq;
using NINA.ObservatoryPlanner.Core;
using NINA.ObservatoryPlanner.Nina;
using NINA.ObservatoryPlanner.UI;
using NINA.Profile.Interfaces;
using NUnit.Framework;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NINA.ObservatoryPlanner.Tests {

    /// <summary>
    /// Saves pictures of the panel at several dock sizes to look at the layout (NINA's dark theme colours are imitated).
    /// Run it by hand: set OP_RENDER_DIR to a folder, then run this explicit test.
    /// </summary>
    [TestFixture, Apartment(ApartmentState.STA), NonParallelizable, Explicit("Saves pictures of the panel for a person to look at")]
    public class PanelRenderTests {

        [Test]
        public void Render_the_panel_at_dock_sizes() {
            var folder = Environment.GetEnvironmentVariable("OP_RENDER_DIR") ?? Path.Combine(Path.GetTempPath(), "op-render");
            Directory.CreateDirectory(folder);
            var root = Path.Combine(Path.GetTempPath(), "op-render-" + Guid.NewGuid().ToString("N"));
            var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Theme(app.Resources);
            try {
                var planner = new PlannerService(null, root);
                var a = planner.NewTarget("Barnard 175");
                a.RaHours = 22.33; a.DecDegrees = 58.93;
                a.Exposures.Clear();
                a.Exposures.Add(new PlannerExposure { Filter = "Ha", ExposureTime = 300, Count = 100, Done = 85 });
                a.Exposures.Add(new PlannerExposure { Filter = "OIII", ExposureTime = 300, Count = 100, Done = 100 });
                a.Exposures.Add(new PlannerExposure { Filter = "SII", ExposureTime = 300, Count = 100, Done = 3 });
                planner.Targets.Add(a);
                var b = planner.NewTarget("M 33 Triangulum Galaxy with NGC 604 region");
                b.Order = ExposureOrder.RotateThroughFilters;
                planner.Targets.Add(b);
                planner.Targets.Add(planner.NewTarget("NGC 7000"));

                var vm = new PlannerDockableVM(new Mock<IProfileService>().Object, planner);
                vm.SelectedTarget = a;
                var template = (DataTemplate)new PlannerView()["NINA.ObservatoryPlanner.UI.PlannerDockableVM_Dockable"];
                foreach (var tab in new[] { 0, 1, 2, 3 }) {
                    vm.SelectedPlannerTab = tab;
                    foreach (var (w, h) in new[] { (360, 760), (500, 760), (820, 760), (1200, 760) }) {
                        Save(Path.Combine(folder, $"tab{tab}-{w}.png"), vm, template, w, h);
                    }
                }
                planner.Options.PanelTextSize = 15;
                vm.SelectedPlannerTab = 0;
                Save(Path.Combine(folder, "tab0-500-text15.png"), vm, template, 500, 760);
            } finally {
                try { Directory.Delete(root, true); } catch (IOException) { }
            }
        }

        /// <summary>
        /// Writes the example pictures of the Info guide (UI/Images/info-*.png in the plugin) from the real panel template.
        /// Run it by hand after changing the panel, then rebuild the plugin.
        /// </summary>
        [Test]
        public void Make_the_info_guide_pictures() {
            var images = Path.Combine(RepoRoot(), "src", "NINA.ObservatoryPlanner", "UI", "Images");
            Directory.CreateDirectory(images);
            var root = Path.Combine(Path.GetTempPath(), "op-render-" + Guid.NewGuid().ToString("N"));
            var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Theme(app.Resources);
            try {
                var planner = new PlannerService(null, root);
                planner.Options.PanelTextSize = 12;
                var a = planner.NewTarget("Barnard 175");
                a.RaHours = 22.33; a.DecDegrees = 58.93;
                a.DelayFirst = 5;
                a.Exposures.Clear();
                a.Exposures.Add(new PlannerExposure { Filter = "Ha", ExposureTime = 300, Count = 100, Done = 85 });
                a.Exposures.Add(new PlannerExposure { Filter = "OIII", ExposureTime = 300, Count = 100, Done = 100 });
                a.Exposures.Add(new PlannerExposure { Filter = "SII", ExposureTime = 300, Count = 100, Done = 3 });
                planner.Targets.Add(a);
                var b = planner.NewTarget("M 33");
                b.Start = new TimeConstraint { Enabled = true, By = ConstraintBy.Altitude, Altitude = 35 };
                planner.Targets.Add(b);
                var c = planner.NewTarget("NGC 7000 North America Nebula");
                c.Enabled = false;
                planner.Targets.Add(c);

                // looks like a run imaging the first target (pictures only)
                void Set(object o, string name, object value) =>
                    o.GetType().GetProperty(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)!
                        .SetValue(o, value);
                Set(planner, nameof(PlannerService.IsRunning), true);
                Set(planner, nameof(PlannerService.Phase), PlannerPhase.Imaging);
                Set(planner, nameof(PlannerService.IsTakingFrames), true);
                Set(planner, nameof(PlannerService.CurrentTarget), a);
                Set(planner, nameof(PlannerService.StatusText), "Imaging Barnard 175 · Ha 300 s · frame 86 of 100");

                var vm = new PlannerDockableVM(new Mock<IProfileService>().Object, planner);
                typeof(PlannerDockableVM).GetField("safe", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(vm, (bool?)true);
                vm.SelectedTarget = a;
                var template = (DataTemplate)new PlannerView()["NINA.ObservatoryPlanner.UI.PlannerDockableVM_Dockable"];

                vm.SelectedPlannerTab = 0;
                Capture(vm, template, 560, 900, content => new FrameworkElement[] {
                    Find<Border>(content, e => e.Parent is Grid g && Equals(g.Tag, "OP_Root") && Grid.GetRow(e) == 0),
                    Find<SideBySidePanel>(content, _ => true).Children[0] as FrameworkElement,
                    Find<SideBySidePanel>(content, _ => true).Children[1] as FrameworkElement
                }, new[] { "info-top.png", "info-targets.png", "info-exposures.png" }.Select(n => Path.Combine(images, n)).ToArray());

                vm.SelectedPlannerTab = 1;
                vm.Stages[0].Lines = new[] {
                    new StageLine(1, "Switch", "Set Switch Value", "Mount power on"), new StageLine(2, "Switch", "Set Switch Value", "Camera power on"),
                    new StageLine(3, "Connect", "Connect All Equipment", null), new StageLine(4, "Camera", "Cool Camera", "-10 °C"),
                    new StageLine(5, "Dome", "Open Dome Shutter", null), new StageLine(6, "Telescope", "Unpark Scope", null)
                };
                vm.Stages[0].EmptyText = null;
                vm.Stages[0].Warnings = new[] { "\"Set Switch Value\" runs before the switch hub is connected, so it fails. Add \"Connect Equipment\" (Switch) above it, or keep the switch hub connected (⚙ Options › Keep connected)." };
                Capture(vm, template, 560, 900, content => new FrameworkElement[] {
                    Find<Expander>(content, _ => true)
                }, new[] { Path.Combine(images, "info-stage.png") });
            } finally {
                try { Directory.Delete(root, true); } catch (IOException) { }
            }
        }

        private static string RepoRoot() {
            var d = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "NINA.ObservatoryPlanner.slnx"))) { d = d.Parent; }
            return d?.FullName ?? throw new DirectoryNotFoundException("repository root");
        }

        private static T Find<T>(DependencyObject d, Func<T, bool> match) where T : DependencyObject {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) {
                var c = VisualTreeHelper.GetChild(d, i);
                if (c is T t && match(t)) { return t; }
                var r = Find(c, match);
                if (r != null) { return r; }
            }
            return null;
        }

        /// <summary>Lays the panel out at <paramref name="width"/> and saves the picked parts at 1.5× for a sharp picture.</summary>
        private static void Capture(object vm, DataTemplate template, int width, int height, Func<FrameworkElement, FrameworkElement[]> parts, string[] paths) {
            var content = new ContentControl { Content = vm, ContentTemplate = template };
            var w = new Window {
                Content = content, Width = width, Height = height, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000,
                Background = (Brush)Application.Current.Resources["BackgroundBrush"]
            };
            w.Show();
            w.UpdateLayout();
            var elements = parts(content);
            for (var i = 0; i < elements.Length; i++) {
                var e = elements[i];
                var size = e.RenderSize;
                const double scale = 1.5;
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen()) {
                    dc.DrawRectangle((Brush)Application.Current.Resources["BackgroundBrush"], null, new Rect(size));
                    dc.DrawRectangle(new VisualBrush(e) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(size));
                }
                bitmap.Render(visual);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(paths[i]);
                encoder.Save(file);
            }
            w.Close();
        }

        private static void Save(string path, object vm, DataTemplate template, int width, int height) {
            var content = new ContentControl { Content = vm, ContentTemplate = template };
            var w = new Window {
                Content = content, Width = width, Height = height, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000,
                Background = (Brush)Application.Current.Resources["BackgroundBrush"]
            };
            w.Show();
            w.UpdateLayout();
            content.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen()) {
                dc.DrawRectangle((Brush)Application.Current.Resources["BackgroundBrush"], null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
                dc.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
            }
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(path)) { encoder.Save(file); }
            w.Close();
        }

        /// <summary>Close to NINA's dark theme.</summary>
        private static void Theme(ResourceDictionary r) {
            SolidColorBrush B(uint rgb) => new(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
            r["BackgroundBrush"] = B(0x17181B);
            r["SecondaryBackgroundBrush"] = B(0x202226);
            r["TertiaryBackgroundBrush"] = B(0x2A2D33);
            r["BorderBrush"] = B(0x3A3E45);
            r["PrimaryBrush"] = B(0xD9DCE0);
            r["ButtonBackgroundBrush"] = B(0x262A30);
            r["ButtonForegroundBrush"] = B(0xD9DCE0);
            r[typeof(TextBlock)] = new Style(typeof(TextBlock)) { Setters = { new Setter(TextBlock.ForegroundProperty, B(0xD9DCE0)) } };
            r[typeof(TextBox)] = new Style(typeof(TextBox)) {
                Setters = {
                    new Setter(Control.BackgroundProperty, B(0x17181B)), new Setter(Control.ForegroundProperty, B(0xD9DCE0)),
                    new Setter(Control.BorderBrushProperty, B(0x3A3E45)), new Setter(Control.PaddingProperty, new Thickness(2, 1, 2, 1))
                }
            };
            // a dark drop-down like NINA's (the default Windows one is light)
            var combo = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
                "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='ComboBox'>" +
                "<Border Background='#17181B' BorderBrush='#3A3E45' BorderThickness='1' CornerRadius='3' Padding='5,2'>" +
                "<DockPanel><TextBlock DockPanel.Dock='Right' Margin='4,0,0,0' VerticalAlignment='Center' FontSize='9' Foreground='#B7BCC3' Text='▼' />" +
                "<TextBlock VerticalAlignment='Center' Foreground='#D9DCE0' Text='{TemplateBinding Text}' TextTrimming='CharacterEllipsis' /></DockPanel></Border></ControlTemplate>");
            r[typeof(ComboBox)] = new Style(typeof(ComboBox)) { Setters = { new Setter(Control.TemplateProperty, combo), new Setter(ComboBox.IsEditableProperty, true), new Setter(ComboBox.IsReadOnlyProperty, true) } };
            r[typeof(TabItem)] = new Style(typeof(TabItem)) { Setters = { new Setter(Control.ForegroundProperty, B(0xD9DCE0)) } };
        }
    }
}
