using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Mirador.App.ViewModels;

/// <summary>
/// Passes a value through unchanged. A two-way <c>x:Bind</c> of <c>ComboBox.SelectedItem</c> (an object) to a typed
/// property needs a converter for the way back.
/// </summary>
public sealed class PassThroughConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, string language) => value;

    public object? ConvertBack(object? value, Type targetType, object? parameter, string language) => value;
}

/// <summary>
/// True to Visible, anything else to Collapsed. An InfoBar's own <c>IsOpen</c> bound through it to its
/// <c>Visibility</c> makes a closed bar take no room: a closed InfoBar is otherwise still there, empty, and a
/// StackPanel puts its spacing around it.
/// </summary>
public sealed class VisibleWhenTrueConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, string language) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, string language) =>
        value is Visibility.Visible;
}
