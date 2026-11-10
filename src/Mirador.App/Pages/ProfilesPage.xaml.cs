using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Mirador.App.Services;
using Mirador.App.ViewModels;

namespace Mirador.App.Pages;

public sealed partial class ProfilesPage : Page
{
    public ProfilesPage()
    {
        ViewModel = new ProfilesViewModel(App.Current.Host);
        InitializeComponent();
    }

    public ProfilesViewModel ViewModel { get; }

    // An instance method: x:Bind calls functions on the page instance.
    public Visibility ShowLayerFolder(bool isDreamAir, bool fromFolder) =>
        isDreamAir && fromFolder ? Visibility.Visible : Visibility.Collapsed;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.Attach();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.Detach();
        base.OnNavigatedFrom(e);
    }

    private void OnMessageClosed(InfoBar sender, InfoBarClosedEventArgs args) => ViewModel.DismissMessage();

    private void OnCheckBuildTools(object sender, RoutedEventArgs e) => App.Current.MainWindow.ShowBuildTools();

    private async void OnBrowseMonado(object sender, RoutedEventArgs e)
    {
        if (await Pickers.FolderAsync() is { } folder)
        {
            ViewModel.MonadoFolder = folder;
        }
    }

    private async void OnBrowseLayer(object sender, RoutedEventArgs e)
    {
        if (await Pickers.FolderAsync() is { } folder)
        {
            ViewModel.LayerFolder = folder;
        }
    }

    private void OnRemoveVariable(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is EnvRow row)
        {
            ViewModel.RemoveVariableCommand.Execute(row);
        }
    }

    private async void OnRename(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Selected is not { } selected)
        {
            return;
        }

        var box = new TextBox { Text = selected.Name, SelectionStart = 0, SelectionLength = selected.Name.Length };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Rename profile",
            Content = box,
            PrimaryButtonText = "Rename",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            ViewModel.Rename(box.Text);
        }
    }

    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Selected is not { } selected)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Delete {selected.Name}?",
            Content = "The profile goes for good.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            ViewModel.Delete();
        }
    }
}
