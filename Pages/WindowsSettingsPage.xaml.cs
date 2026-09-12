using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using stellarisKIT.ViewModels;
using System;
using Windows.Storage.Pickers;

namespace stellarisKIT.Pages;

public sealed partial class WindowsSettingsPage : Page
{
    public WindowsSettingsViewModel ViewModel => (WindowsSettingsViewModel)DataContext;
    public WindhawkProvisioningViewModel Windhawk => ViewModel.Windhawk;

    public WindowsSettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => _ = ViewModel.LoadAsync();
        Loaded += (_, _) => Windhawk.RefreshDetection();
    }

    private void ServiceToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (ViewModel.CanEdit)
            _ = ViewModel.ToggleServiceCommand.ExecuteAsync(null);
        else
            _ = ViewModel.LoadAsync();
    }

    private void PowerToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (ViewModel.CanEdit)
            _ = ViewModel.SavePowerSettingsCommand.ExecuteAsync(null);
        else
            _ = ViewModel.LoadAsync();
    }

    private async void StartWindhawk_Click(object sender, RoutedEventArgs e)
    {
        // Confirmation before installing anything or overwriting settings.
        var dialog = new ContentDialog
        {
            Title = "Install Windhawk & import KaliteOS settings?",
            Content = "Continue?",
            PrimaryButtonText = "Continue",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        await Windhawk.RunProvisioningAsync();
    }

    private async void TestImport_Click(object sender, RoutedEventArgs e)
    {
        // Test import — no reinstall, just windhawk-cli data import (AutoOS pattern) + mod updates using KaliteOS.json.
        var dialog = new ContentDialog
        {
            Title = "Test import KaliteOS settings?",
            Content = "Continue?",
            PrimaryButtonText = "Continue",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        await Windhawk.TestImportAsync();
    }
}
