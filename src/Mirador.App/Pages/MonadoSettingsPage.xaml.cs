using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Mirador.App.ViewModels;

namespace Mirador.App.Pages;

public sealed partial class MonadoSettingsPage : Page
{
    public MonadoSettingsPage()
    {
        ViewModel = new MonadoSettingsViewModel(App.Current.Host, App.Current.Ui);
        InitializeComponent();

        // A slider saves when it is let go: its thumb holds the pointer while dragged, so these come after the drag.
        foreach (var slider in new[] { LighthouseSlider, ScaleSlider, BudgetSlider })
        {
            slider.AddHandler(PointerCaptureLostEvent, new PointerEventHandler((_, _) => ViewModel.Save()), handledEventsToo: true);
            slider.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => ViewModel.Save()), handledEventsToo: true);
            slider.AddHandler(KeyUpEvent, new KeyEventHandler((_, _) => ViewModel.Save()), handledEventsToo: true);
        }
    }

    public MonadoSettingsViewModel ViewModel { get; }

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

    private async void OnReset(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Reset the Monado settings?",
            Content = "Lighthouse wait time, render scale, compositor budget and stop-when-the-last-app-closes go back to their defaults, for every profile.",
            PrimaryButtonText = "Reset",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            ViewModel.ResetToDefaults();
        }
    }
}
