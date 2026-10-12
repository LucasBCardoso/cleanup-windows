namespace Cleanup.App.Services;

/// <summary>The top tab bar: Hoje · Limpeza · Apps · Espaço · Sistema.</summary>
public enum AppTab { Home, Cleaning, Apps, Space, System }

public enum Module
{
    Home, SmartScan, LargeFiles, Duplicates, Developer, Privacy, Uninstaller, Updater, Extensions, SpaceLens, Optimization, Maintenance,
}

public static class Modules
{
    /// <summary>The sparkles mark (U+2726), drawn with Segoe UI Symbol: MDL2 has no sparkle glyph.</summary>
    public const string Sparkles = "✦";

    public static string Title(this AppTab tab) => tab switch
    {
        AppTab.Home => "Hoje",
        AppTab.Cleaning => "Limpeza",
        AppTab.Apps => "Apps",
        AppTab.Space => "Espaço",
        _ => "Sistema",
    };

    /// <summary>Segoe Fluent Icons / MDL2 glyphs standing in for the macOS SF Symbols.</summary>
    public static string Glyph(this AppTab tab) => tab switch
    {
        AppTab.Home => "\uE80F",      // Home
        AppTab.Cleaning => Sparkles,
        AppTab.Apps => "\uE71D",      // AllApps
        AppTab.Space => "\uEB05",     // PieSingle
        _ => "\uE713",                // Settings
    };

    public static string Title(this Module module) => module switch
    {
        Module.Home => "Hoje",
        Module.SmartScan => "Smart Scan",
        Module.LargeFiles => "Arquivos grandes",
        Module.Duplicates => "Duplicados",
        Module.Developer => "Desenvolvedor",
        Module.Privacy => "Privacidade",
        Module.Uninstaller => "Desinstalador",
        Module.Updater => "Atualizador",
        Module.Extensions => "Extensões",
        Module.SpaceLens => "Space Lens",
        Module.Optimization => "Otimização",
        _ => "Manutenção",
    };

    public static string Glyph(this Module module) => module switch
    {
        Module.Home => "\uE80F",
        Module.SmartScan => Sparkles,
        Module.LargeFiles => "\uE8A5",   // Document
        Module.Duplicates => "\uE8C8",   // Copy
        Module.Developer => "\uE943",    // Code
        Module.Privacy => "\uE72E",      // Lock
        Module.Uninstaller => "\uE74D",  // Delete
        Module.Updater => "\uE896",      // Download
        Module.Extensions => "\uEA86",   // Puzzle
        Module.SpaceLens => "\uEB05",    // PieSingle
        Module.Optimization => "\uEC4A", // SpeedHigh
        _ => "\uE90F",                   // Repair
    };

    public static AppTab Tab(this Module module) => module switch
    {
        Module.Home => AppTab.Home,
        Module.SmartScan or Module.LargeFiles or Module.Duplicates or Module.Developer or Module.Privacy => AppTab.Cleaning,
        Module.Uninstaller or Module.Updater or Module.Extensions => AppTab.Apps,
        Module.SpaceLens => AppTab.Space,
        _ => AppTab.System,
    };

    public static IReadOnlyList<Module> InTab(AppTab tab) => Enum.GetValues<Module>().Where(m => m.Tab() == tab).ToList();
}
