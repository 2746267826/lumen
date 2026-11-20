using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using WinForms = System.Windows.Forms;

namespace Lumen.Windows.Services;

public sealed class TrayIcon : IDisposable
{
    private readonly Window _window;
    private readonly WinForms.NotifyIcon _icon;
    private readonly Func<string> _statusProvider;
    private bool _disposed;

    public TrayIcon(Window window, Func<string> statusProvider, Action exit)
    {
        _window = window;
        _statusProvider = statusProvider;

        _icon = new WinForms.NotifyIcon
        {
            Text = "Lumen 流明互传",
            Icon = LoadIcon(),
            Visible = true,
            ContextMenuStrip = null
        };
        _icon.DoubleClick += (_, _) => Restore();
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Right)
            {
                ShowMenu(exit);
            }
        };
    }

    public void Restore()
    {
        if (!_window.IsVisible || _window.WindowState == WindowState.Minimized)
        {
            _window.Show();
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
        _window.Topmost = true;
        _window.Topmost = false;
        _window.Focus();
    }

    public void Notify(string title, string message)
    {
        try
        {
            _icon.ShowBalloonTip(4000, title, message, WinForms.ToolTipIcon.Info);
        }
        catch
        {
        }
    }

    public void UpdateTooltip(string text)
    {
        try
        {
            _icon.Text = text.Length > 63 ? text[..63] : text;
        }
        catch
        {
        }
    }

    private void ShowMenu(Action exit)
    {
        var menu = new ContextMenu
        {
            Placement = PlacementMode.MousePoint,
            StaysOpen = false
        };

        var open = new MenuItem { Header = "打开 Lumen" };
        open.Click += (_, _) => Restore();

        var status = new MenuItem
        {
            Header = _statusProvider(),
            IsEnabled = false,
            FontSize = 11
        };

        var quit = new MenuItem { Header = "退出 Lumen" };
        quit.Click += (_, _) =>
        {
            Dispose();
            exit();
        };

        menu.Items.Add(open);
        menu.Items.Add(new Separator());
        menu.Items.Add(status);
        menu.Items.Add(new Separator());
        menu.Items.Add(quit);
        menu.IsOpen = true;
    }

    private static System.Drawing.Icon LoadIcon()
    {
        var path = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(path))
        {
            try
            {
                var extracted = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (extracted is not null)
                {
                    return extracted;
                }
            }
            catch
            {
            }
        }

        return System.Drawing.SystemIcons.Application;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _icon.Visible = false;
            _icon.Dispose();
        }
        catch
        {
        }
    }
}
