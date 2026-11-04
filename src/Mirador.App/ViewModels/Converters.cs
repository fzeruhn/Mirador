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
