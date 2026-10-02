using System;
using System.Threading.Tasks;

namespace NINA.ObservatoryPlanner.Nina {

    /// <summary>Runs work on the WPF UI thread (sequence collections are bound to the sequencer view). Inline when there is no UI.</summary>
    internal static class Ui {
        public static Task Run(Action action) {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess()) {
                action();
                return Task.CompletedTask;
            }
            return dispatcher.InvokeAsync(action).Task;
        }

        public static Task<T> Run<T>(Func<T> func) {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess()) { return Task.FromResult(func()); }
            return dispatcher.InvokeAsync(func).Task;
        }
    }
}
