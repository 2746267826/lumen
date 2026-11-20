using System.Windows;
using System.Windows.Threading;

namespace Lumen.Windows.Infrastructure;

public static class UiThread
{
    public static Dispatcher? Dispatcher =>
        Application.Current?.Dispatcher;

    public static void Post(Action action)
    {
        var dispatcher = Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.BeginInvoke(action, DispatcherPriority.Normal);
    }

    public static Task InvokeAsync(Action action)
    {
        var dispatcher = Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(action).Task;
    }

    public static Task<T> InvokeAsync<T>(Func<T> action)
    {
        var dispatcher = Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            return Task.FromResult(action());
        }

        return dispatcher.InvokeAsync(action).Task;
    }
}
