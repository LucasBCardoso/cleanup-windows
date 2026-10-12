using Cleanup.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Cleanup.App.Views;

/// <summary>Placeholder for a module that hasn't been converted yet.</summary>
public sealed partial class ComingSoonView : UserControl
{
    public ComingSoonView(Module module)
    {
        Content = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 10,
            Children =
            {
                new FontIcon { Glyph = module.Glyph(), FontSize = 36, Opacity = 0.6 },
                new TextBlock { Text = module.Title(), Style = (Style)Application.Current.Resources["ModuleTitleStyle"], HorizontalAlignment = HorizontalAlignment.Center },
                new TextBlock { Text = "Este módulo ainda está sendo convertido para o Windows.", Style = (Style)Application.Current.Resources["BodyTextStyle"], HorizontalAlignment = HorizontalAlignment.Center },
            },
        };
    }
}
