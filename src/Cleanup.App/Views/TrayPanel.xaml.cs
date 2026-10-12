using Cleanup.App.Services;
using Cleanup.App.ViewModels;
using Cleanup.Core.Health;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace Cleanup.App.Views;

public sealed partial class TrayPanel : Window
{
    public HealthViewModel Health => AppViewModels.Health!;

    private const int PanelWidth = 360;
    private const int PanelHeight = 560;

    public TrayPanel()
    {
        InitializeComponent();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Cleanup.ico"));
        AppWindow.IsShownInSwitchers = false;
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.SetBorderAndTitleBar(true, false);
            p.IsResizable = false;
            p.IsMaximizable = false;
            p.IsMinimizable = false;
            p.IsAlwaysOnTop = true;
        }
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated) AppWindow.Hide();
        };
    }

    public void ApplyTheme(ElementTheme theme) => Root.RequestedTheme = theme;

    /// <summary>Above the clock: the bottom-right corner of the work area of the display the cursor is on.</summary>
    public void ShowNearTray()
    {
        var area = DisplayArea.GetFromPoint(CursorPosition(), DisplayAreaFallback.Primary).WorkArea;
        var scale = (Content as FrameworkElement)?.XamlRoot?.RasterizationScale ?? 1.0;
        var width = (int)(PanelWidth * scale);
        var height = (int)(PanelHeight * scale);
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(area.X + area.Width - width - 12, area.Y + area.Height - height - 12, width, height));
        AppWindow.Show();
        Activate();
    }

    private static Windows.Graphics.PointInt32 CursorPosition()
    {
        GetCursorPos(out var p);
        return new Windows.Graphics.PointInt32(p.X, p.Y);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    public Visibility Show(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        AppWindow.Hide();
        App.ShowMainWindow();
    }

    private void OnScan(object sender, RoutedEventArgs e)
    {
        AppWindow.Hide();
        App.ShowMainWindow("cleanup://scan");
    }

    private void OnIssueAction(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not IssueRow row) return;
        AppWindow.Hide();
        App.ShowMainWindow(row.Issue.Action == IssueAction.SmartScan ? "cleanup://scan" : "cleanup://open/optimization");
    }

    private void Snooze(object sender, SnoozeDuration duration)
    {
        if ((sender as FrameworkElement)?.Tag is IssueRow row) Health.Snooze(row, duration);
    }

    private void OnSnoozeDay(object sender, RoutedEventArgs e) => Snooze(sender, SnoozeDuration.Day);
    private void OnSnoozeWeek(object sender, RoutedEventArgs e) => Snooze(sender, SnoozeDuration.Week);
    private void OnSnoozeForever(object sender, RoutedEventArgs e) => Snooze(sender, SnoozeDuration.Forever);
}
