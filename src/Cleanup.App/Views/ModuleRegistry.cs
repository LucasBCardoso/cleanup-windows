using Cleanup.App.Services;
using Microsoft.UI.Xaml;

namespace Cleanup.App.Views;

/// <summary>
/// Views for the modules beyond the core four. Each module adds one line here; until then its tab shows
/// <see cref="ComingSoonView"/>.
/// </summary>
public static class ModuleRegistry
{
    public static UIElement? Create(Module module) => module switch
    {
        Module.Duplicates => new DuplicatesView(),
        Module.Developer => new DeveloperView(),
        _ => null,
    };
}
