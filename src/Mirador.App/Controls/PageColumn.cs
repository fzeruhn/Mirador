using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Mirador.App.Controls;

/// <summary>
/// Lays a page's content out in one column at most <see cref="MaxColumnWidth"/> wide: centred, or at the left. XAML's
/// own <c>MaxWidth</c> on stretched content places it by the width the content asks for, not the width it is given, so
/// content whose text is short (Home with one message) ended up pushed to the right and cut off.
/// </summary>
public sealed partial class PageColumn : Panel
{
    public static readonly DependencyProperty MaxColumnWidthProperty = DependencyProperty.Register(
        nameof(MaxColumnWidth), typeof(double), typeof(PageColumn), new PropertyMetadata(900.0, OnLayoutChanged));

    public static readonly DependencyProperty IsCenteredProperty = DependencyProperty.Register(
        nameof(IsCentered), typeof(bool), typeof(PageColumn), new PropertyMetadata(true, OnLayoutChanged));

    /// <summary>The column's widest; narrower when there is less room.</summary>
    public double MaxColumnWidth
    {
        get => (double)GetValue(MaxColumnWidthProperty);
        set => SetValue(MaxColumnWidthProperty, value);
    }

    /// <summary>Whether the column is centred in the room there is (else at its left).</summary>
    public bool IsCentered
    {
        get => (bool)GetValue(IsCenteredProperty);
        set => SetValue(IsCenteredProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = Math.Min(availableSize.Width, MaxColumnWidth);
        var height = 0.0;
        var widest = 0.0;
        foreach (var child in Children)
        {
            child.Measure(new Size(width, availableSize.Height));
            height = Math.Max(height, child.DesiredSize.Height);
            widest = Math.Max(widest, child.DesiredSize.Width);
        }

        // All the room there is, so that the column's place never depends on what its content asks for.
        return new Size(double.IsInfinity(availableSize.Width) ? widest : availableSize.Width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var width = Math.Min(finalSize.Width, MaxColumnWidth);
        var x = IsCentered ? (finalSize.Width - width) / 2 : 0;
        foreach (var child in Children)
        {
            child.Arrange(new Rect(x, 0, width, finalSize.Height));
        }

        return finalSize;
    }

    private static void OnLayoutChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((PageColumn)d).InvalidateMeasure();
}
