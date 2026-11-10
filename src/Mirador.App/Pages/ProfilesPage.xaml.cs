using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Mirador.App.Services;
using Mirador.App.ViewModels;
using Windows.System;
using Windows.UI.Core;

namespace Mirador.App.Pages;

public sealed partial class ProfilesPage : Page
{
    public ProfilesPage()
    {
        ViewModel = new ProfilesViewModel(App.Current.Host);
        InitializeComponent();
    }

    public ProfilesViewModel ViewModel { get; }

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

    /// <summary>
    /// The closed picker moves its pick with the keyboard, and picking "Add a profile…" adds one: there the keys stop at
    /// the last profile, so a profile is only ever added from the open drop-down. (Typing to find an item is off for the
    /// same reason: it could land on "Add a profile…".)
    /// </summary>
    private void OnPickerPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Alt+Down opens the drop-down: left to the picker.
        if (sender is not ComboBox { IsDropDownOpen: false } picker
            || InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(CoreVirtualKeyStates.Down))
        {
            return;
        }

        var lastProfile = ViewModel.Profiles.IndexOf(ProfilesViewModel.AddItem) - 1;
        switch (e.Key)
        {
            case VirtualKey.Down or VirtualKey.GamepadDPadDown or VirtualKey.GamepadLeftThumbstickDown
                when picker.SelectedIndex >= lastProfile:
                e.Handled = true;
                break;
            case VirtualKey.End or VirtualKey.PageDown when lastProfile >= 0:
                e.Handled = true;
                picker.SelectedIndex = lastProfile;
                break;
        }
    }

    private void OnMessageClosed(InfoBar sender, InfoBarClosedEventArgs args) => ViewModel.DismissMessage();

    private void OnCheckBuildTools(object sender, RoutedEventArgs e) => App.Current.MainWindow.ShowBuildTools();

    private void OnOpenDiagnostics(object sender, RoutedEventArgs e)
    {
        DiagnosticsPage.VerifyNext = (ViewModel.Verify.ProfileId, ViewModel.Verify.LastLogFile);
        App.Current.MainWindow.Navigate("Diagnostics");
    }

    private async void OnBrowseMonado(object sender, RoutedEventArgs e)
    {
        if (await Pickers.FolderAsync() is { } folder)
        {
            ViewModel.MonadoFolder = folder;
        }
    }

    private void OnRemoveVariable(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is EnvRow row)
        {
            ViewModel.RemoveVariableCommand.Execute(row);
        }
    }

    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Selected is not { } selected || !ViewModel.CanDelete)
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
            await ViewModel.DeleteAsync();
        }
    }
}
