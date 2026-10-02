using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;

namespace NINA.ObservatoryPlanner.UI.Dialogs {

    public sealed class AskButton {
        public string Label { get; init; }
        public string Result { get; init; }
        public bool IsDefault { get; init; }
        public bool IsCancel { get; init; }
        public Style Style { get; init; }
    }

    public sealed class AskChoice : INotifyPropertyChanged {
        private bool isSelected;
        public string Name { get; init; }
        public string Description { get; init; }
        public bool IsSelected { get => isSelected; set { isSelected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); } }
        public event PropertyChangedEventHandler PropertyChanged;
    }

    /// <summary>
    /// The planner's dialog, in NINA's colours: a message with buttons, optionally a list to pick from,
    /// a text box (Save as) or a "Don't ask me again" check box.
    /// </summary>
    public partial class AskWindow : Window, INotifyPropertyChanged {
        private string textValue;
        private string textError;
        private bool isChecked;

        public AskWindow() {
            InitializeComponent();
            DataContext = this;
            Owner = Application.Current?.MainWindow is { IsLoaded: true } main && main != this ? main : null;
            if (Owner == null) { WindowStartupLocation = WindowStartupLocation.CenterScreen; }
            Loaded += (_, _) => { if (HasText) { textBox.Focus(); textBox.SelectAll(); } };
        }

        public string Heading { get; init; }
        public string Message { get; init; }
        public IReadOnlyList<AskButton> Buttons { get; init; } = Array.Empty<AskButton>();
        public IReadOnlyList<AskChoice> Choices { get; init; } = Array.Empty<AskChoice>();
        public string CheckText { get; init; }
        public bool HasCheck => !string.IsNullOrEmpty(CheckText);
        public bool IsChecked { get => isChecked; set { isChecked = value; Raise(); } }
        public string TextLabel { get; init; }
        public bool HasText => TextLabel != null;
        public string TextValue { get => textValue; set { textValue = value; TextError = Validate?.Invoke(value); Raise(); } }
        public string TextError { get => textError; private set { textError = value; Raise(); } }
        /// <summary>Returns an error message for the text, or null when it is fine.</summary>
        public Func<string, string> Validate { get; init; }
        public string Result { get; private set; }
        public AskChoice SelectedChoice => Choices.FirstOrDefault(c => c.IsSelected);

        /// <summary>Answers as if the button with this result was clicked (test hook).</summary>
        public void Answer(string result) {
            var button = Buttons.FirstOrDefault(b => b.Result == result) ?? Buttons.First(b => b.IsCancel);
            Result = button.Result;
            DialogResult = !button.IsCancel;
        }

        private void Answer_Click(object sender, RoutedEventArgs e) {
            var button = (AskButton)((FrameworkElement)sender).DataContext;
            if (!button.IsCancel && HasText) {
                TextError = Validate?.Invoke(TextValue);
                if (TextError != null) { return; }
            }
            Result = button.Result;
            DialogResult = !button.IsCancel;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void Raise([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>Short forms of the planner's dialogs.</summary>
    public static class Ask {
        private static Style S(string key) => Application.Current?.TryFindResource(key) as Style;
        public static AskButton Primary(string label, string result = null) => new() { Label = label, Result = result ?? label, IsDefault = true, Style = S("OP_Primary") };
        public static AskButton Danger(string label, string result = null) => new() { Label = label, Result = result ?? label, IsDefault = true, Style = DangerStyle() };
        public static AskButton Plain(string label, string result = null) => new() { Label = label, Result = result ?? label, Style = S("OP_Btn") };
        public static AskButton Cancel(string label = "Cancel") => new() { Label = label, Result = "cancel", IsCancel = true, Style = S("OP_Btn") };

        private static Style DangerStyle() {
            var basis = S("OP_Primary");
            if (basis == null) { return null; }
            var style = new Style(typeof(System.Windows.Controls.Button), basis);
            style.Setters.Add(new Setter(System.Windows.Controls.Control.BackgroundProperty, PlannerBrushes.Unsafe));
            style.Setters.Add(new Setter(System.Windows.Controls.Control.BorderBrushProperty, PlannerBrushes.Unsafe));
            return style;
        }

        /// <summary>Delete confirmation with "Don't ask me again". Returns whether to delete, and the check box.</summary>
        public static (bool Yes, bool DontAskAgain) Confirm(string title, string message, string yes = "Delete", bool offerDontAsk = true) {
            var w = new AskWindow {
                Heading = title, Message = message, CheckText = offerDontAsk ? "Don't ask me again" : null,
                Buttons = new[] { Danger(yes, "yes"), Cancel() }
            };
            var ok = w.ShowDialog() == true && w.Result == "yes";
            return (ok, ok && w.IsChecked);
        }

        public static string Choose(string title, string message, params AskButton[] buttons) {
            var w = new AskWindow { Heading = title, Message = message, Buttons = buttons };
            return w.ShowDialog() == true ? w.Result : "cancel";
        }

        /// <summary>Pick one of <paramref name="choices"/>; returns the button result and the picked choice.</summary>
        public static (string Result, string Choice) Pick(string title, string message, IReadOnlyList<AskChoice> choices, params AskButton[] buttons) {
            var w = new AskWindow { Heading = title, Message = message, Choices = choices, Buttons = buttons };
            return w.ShowDialog() == true ? (w.Result, w.SelectedChoice?.Name) : ("cancel", null);
        }

        public static string Text(string title, string label, string initial, Func<string, string> validate, string ok = "Save") {
            var w = new AskWindow {
                Heading = title, TextLabel = label, Validate = validate,
                Buttons = new[] { Primary(ok, "ok"), Cancel() }
            };
            w.TextValue = initial;
            return w.ShowDialog() == true && w.Result == "ok" ? w.TextValue.Trim() : null;
        }

        public static void Info(string title, string message) => Choose(title, message, Primary("OK"));
    }
}
