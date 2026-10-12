using Cleanup.App.Services;
using Cleanup.App.ViewModels;
using Cleanup.App.Views;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace Cleanup.App;

public sealed partial class MainWindow : Window
{
    private readonly Dictionary<Module, UIElement> views = [];
    private readonly Dictionary<AppTab, Module> lastModule = [];
    private readonly Dictionary<AppTab, ToggleButton> tabButtons = [];
    private readonly AppSettings settings = AppServices.Shared.Settings;

    public Module CurrentModule { get; private set; } = Module.Home;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleArea);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Cleanup.ico"));
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1240, 820));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 960;
            presenter.PreferredMinimumHeight = 640;
        }
        ApplyTheme(settings.Theme);
        RootGrid.ActualThemeChanged += (_, _) => UpdateCaptionButtons();
        BuildTabs();
        var start = Enum.TryParse<Module>(settings.LastModule, out var saved) ? saved : Module.Home;
        Navigate(start);
        Closed += (_, _) =>
        {
            settings.LastModule = CurrentModule.ToString();
            settings.Save();
        };
    }

    public void ApplyTheme(ThemeChoice choice)
    {
        RootGrid.RequestedTheme = choice switch
        {
            ThemeChoice.Daybreak => ElementTheme.Light,
            ThemeChoice.Ledger => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        UpdateCaptionButtons();
    }

    private void UpdateCaptionButtons()
    {
        var dark = RootGrid.ActualTheme == ElementTheme.Dark;
        var bar = AppWindow.TitleBar;
        bar.ButtonForegroundColor = dark ? Colors.White : Colors.Black;
        bar.ButtonHoverForegroundColor = dark ? Colors.White : Colors.Black;
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        bar.ButtonHoverBackgroundColor = dark ? Windows.UI.Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF) : Windows.UI.Color.FromArgb(0x14, 0, 0, 0);
    }

    private void BuildTabs()
    {
        foreach (var tab in Enum.GetValues<AppTab>())
        {
            var button = new ToggleButton
            {
                Style = (Style)Application.Current.Resources["TabButtonStyle"],
                Content = IconLabel(tab.Glyph(), tab.Title()),
            };
            ToolTipService.SetToolTip(button, $"{tab.Title()} (Ctrl+{(int)tab + 1})");
            button.Click += (_, _) => SelectTab(tab);
            tabButtons[tab] = button;
            TabsPanel.Children.Add(button);
        }
    }

    private static StackPanel IconLabel(string glyph, string text) => new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = 7,
        Children = { Icon(glyph), new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center } },
    };

    public static FontIcon Icon(string glyph, double size = 14) => glyph == Modules.Sparkles
        ? new FontIcon { Glyph = glyph, FontSize = size, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe UI Symbol") }
        : new FontIcon { Glyph = glyph, FontSize = size };

    private void SelectTab(AppTab tab) =>
        Navigate(lastModule.TryGetValue(tab, out var module) ? module : Modules.InTab(tab)[0]);

    public void Navigate(Module module)
    {
        CurrentModule = module;
        var tab = module.Tab();
        lastModule[tab] = module;
        foreach (var (t, button) in tabButtons) button.IsChecked = t == tab;

        SubTabsPanel.Children.Clear();
        var modules = Modules.InTab(tab);
        SubTabsPanel.Visibility = modules.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (modules.Count > 1)
        {
            foreach (var m in modules)
            {
                var button = new ToggleButton
                {
                    Style = (Style)Application.Current.Resources["SubTabButtonStyle"],
                    Content = IconLabel(m.Glyph(), m.Title()),
                    IsChecked = m == module,
                };
                button.Click += (_, _) => Navigate(m);
                SubTabsPanel.Children.Add(button);
            }
        }

        if (!views.TryGetValue(module, out var view))
        {
            view = CreateView(module);
            views[module] = view;
        }
        Host.Content = view;
    }

    private static UIElement CreateView(Module module) => module switch
    {
        Module.Home => new HomeView(),
        Module.SmartScan => new SmartScanView(),
        Module.LargeFiles => new LargeFilesView(),
        Module.SpaceLens => new SpaceLensView(),
        _ => ModuleRegistry.Create(module) ?? new ComingSoonView(module),
    };

    /// <summary>A second launch or a <c>cleanup:</c> link: come to the front and follow the link (navigate or read-only scan).</summary>
    public void FollowLink(string? link)
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } p) p.Restore();
        Activate();
        switch (Cleanup.Core.Home.DeepLink.Parse(link))
        {
            case Cleanup.Core.Home.DeepLink.Open open when Enum.TryParse<Module>(open.Module, ignoreCase: true, out var module):
                Navigate(module);
                break;
            case Cleanup.Core.Home.DeepLink.Scan:
                Navigate(Module.SmartScan);
                if (AppViewModels.Scan.ScanCommand.CanExecute(null)) AppViewModels.Scan.ScanCommand.Execute(null);
                break;
            case Cleanup.Core.Home.DeepLink.History:
                _ = ShowHistoryAsync();
                break;
        }
    }

    public void ShowError(string message)
    {
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }

    private void OnTabAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        var index = sender.Key - Windows.System.VirtualKey.Number1;
        if (index >= 0 && index < tabButtons.Count) SelectTab((AppTab)index);
        args.Handled = true;
    }

    private void OnScanAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        Navigate(Module.SmartScan);
        if (AppViewModels.Scan.ScanCommand.CanExecute(null)) AppViewModels.Scan.ScanCommand.Execute(null);
        args.Handled = true;
    }

    private async void OnHistoryAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await ShowHistoryAsync();
    }

    private async void OnHistory(object sender, RoutedEventArgs e) => await ShowHistoryAsync();

    public async Task ShowHistoryAsync()
    {
        var dialog = new HistoryDialog { XamlRoot = RootGrid.XamlRoot, RequestedTheme = RootGrid.ActualTheme };
        await dialog.ShowAsync();
    }

    private async void OnSettings(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsDialog { XamlRoot = RootGrid.XamlRoot, RequestedTheme = RootGrid.ActualTheme };
        await dialog.ShowAsync();
    }
}

/// <summary>The view models that outlive their views (one per module), shared between Hoje and the modules.</summary>
public static class AppViewModels
{
    public static ScanViewModel Scan { get; } = new();
    public static LargeFilesViewModel LargeFiles { get; } = new();
    public static SpaceLensViewModel SpaceLens { get; } = new();
    public static HistoryViewModel History { get; } = new();
}
