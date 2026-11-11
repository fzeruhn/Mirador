using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Mirador.App.Services;
using Mirador.App.ViewModels;

namespace Mirador.App.Pages;

public sealed partial class EyeTrackingPage : Page
{
    public EyeTrackingPage()
    {
        ViewModel = new EyeTrackingViewModel(App.Current.Host);
        InitializeComponent();
    }

    public EyeTrackingViewModel ViewModel { get; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.Attach();
        ViewModel.Refresh();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.Detach();
        base.OnNavigatedFrom(e);
    }

    private void OnBackupMessageClosed(InfoBar sender, InfoBarClosedEventArgs args) => ViewModel.DismissBackupMessage();

    private async void OnRestore(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not BackupItem item)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Restore this calibration?",
            Content = $"The calibration from {item.Text} goes back in. The one in use now is kept in the list.",
            PrimaryButtonText = "Restore",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.RestoreAsync(item);
        }
    }

    private async void OnExport(object sender, RoutedEventArgs e)
    {
        if (await Pickers.SaveFileAsync($"eye-calibration-{ViewModel.SelectedSerial}", "Zip archive", ".zip") is { } path)
        {
            ViewModel.Export(path);
        }
    }

    private async void OnImport(object sender, RoutedEventArgs e)
    {
        if (await Pickers.OpenFileAsync(".zip") is { } path)
        {
            await ViewModel.ImportAsync(path);
        }
    }
}
