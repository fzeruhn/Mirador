using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Mirador.Core.Modules;

namespace Mirador.App.ViewModels;

/// <summary>A profile in a list: its id and its name.</summary>
public sealed record ProfileItem(string Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>A status line on Home, with the icon and colour of its level.</summary>
public sealed record StatusItem(string Label, string Text, StatusLevel Level)
{
    public string Glyph => Level switch
    {
        StatusLevel.Ok => "",
        StatusLevel.Warning => "",
        StatusLevel.Error => "",
        _ => "",
    };

    public Brush Brush => Level switch
    {
        StatusLevel.Ok => Theme("SystemFillColorSuccessBrush"),
        StatusLevel.Warning => Theme("SystemFillColorCautionBrush"),
        StatusLevel.Error => Theme("SystemFillColorCriticalBrush"),
        _ => Theme("TextFillColorSecondaryBrush"),
    };

    public static StatusItem From(StatusLine line) => new(line.Label, line.Text, line.Level);

    internal static Brush Theme(string key) => (Brush)Application.Current.Resources[key];
}

/// <summary>A message shown in an <see cref="InfoBar"/>: a check that failed, a warning, a notice.</summary>
public sealed record NoticeItem(string Key, string Title, string Message, InfoBarSeverity Severity);
