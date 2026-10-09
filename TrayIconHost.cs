using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using Application = System.Windows.Application;

namespace WinOtp;

/// <summary>System tray icon for show/exit when minimize-to-tray is enabled.</summary>
internal sealed class TrayIconHost : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly MainWindow _window;

    public TrayIconHost(MainWindow window)
    {
        _window = window;
        _icon = new NotifyIcon
        {
            Text = "WinOTP",
            Icon = SystemIcons.Application,
            Visible = true
        };
        _icon.DoubleClick += (_, _) => ShowMainWindow();

        var menu = new ContextMenuStrip();
        menu.Items.Add("Show WinOTP", null, (_, _) => ShowMainWindow());
        menu.Items.Add("Exit", null, (_, _) => _window.RequestShutdown());
        _icon.ContextMenuStrip = menu;
    }

    public void ShowMainWindow()
    {
        _window.Dispatcher.Invoke(() =>
        {
            _window.Show();
            _window.WindowState = WindowState.Normal;
            _window.Activate();
        });
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
