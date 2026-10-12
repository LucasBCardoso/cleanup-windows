using Cleanup.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Cleanup.App.Controls;

public sealed partial class SelectionBar : UserControl
{
    public static readonly DependencyProperty ListProperty =
        DependencyProperty.Register(nameof(List), typeof(SelectableList), typeof(SelectionBar), new PropertyMetadata(null, (d, _) => ((SelectionBar)d).Bindings.Update()));

    public SelectableList List
    {
        get => (SelectableList)GetValue(ListProperty);
        set => SetValue(ListProperty, value);
    }

    public SelectionBar() => InitializeComponent();

    public Visibility HasHidden(string? label) => string.IsNullOrEmpty(label) ? Visibility.Collapsed : Visibility.Visible;

    private void OnSelectAllClick(object sender, RoutedEventArgs e)
    {
        List?.ToggleAll();
        // The checkbox shows the list's state, not its own click cycle.
        SelectAll.IsChecked = List?.SelectAllState;
    }
}
