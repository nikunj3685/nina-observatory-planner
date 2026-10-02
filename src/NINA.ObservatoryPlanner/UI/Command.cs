using System;
using System.Windows.Input;

namespace NINA.ObservatoryPlanner.UI {

    internal sealed class Command : ICommand {
        private readonly Action<object> execute;
        private readonly Func<object, bool> canExecute;

        public Command(Action<object> execute, Func<object, bool> canExecute = null) {
            this.execute = execute;
            this.canExecute = canExecute;
        }

        public Command(Action execute) : this(_ => execute()) { }

        public Command(Action execute, Func<object, bool> canExecute) : this(_ => execute(), canExecute) { }

        public event EventHandler CanExecuteChanged {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }

        public bool CanExecute(object parameter) => canExecute?.Invoke(parameter) ?? true;
        public void Execute(object parameter) => execute(parameter);
    }

    public sealed record Choice(object Value, string Label);
}
