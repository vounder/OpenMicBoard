using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenMicBoard.App.Services;
using OpenMicBoard.App.ViewModels;
using Windows.Storage.Pickers;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace OpenMicBoard.App;

/// <summary>
/// The main content page displayed inside the application window.
/// </summary>
public sealed partial class MainPage : Page
{
    private readonly GlobalHotkeyService _hotkeys = new();
    private bool _loaded;

    public MainPageViewModel ViewModel { get; } = new();

    public MainPage()
    {
        InitializeComponent();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        _hotkeys.Attach(App.WindowHandle);
        ViewModel.AttachGlobalHotkeys(_hotkeys);
        await ViewModel.InitializeAsync();
        if (!ViewModel.RequiresOnboarding)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Welcome to OpenMicBoard",
            Content =
                "OpenMicBoard works locally and sends no telemetry. Update checks are optional; " +
                "if enabled, GitHub receives the normal network metadata of a request. " +
                "Choose audio endpoints on the dashboard, then add WAV, MP3, or OGG files.",
            PrimaryButtonText = "Keep offline",
            SecondaryButtonText = "Enable update checks",
            DefaultButton = ContentDialogButton.Primary
        };
        var result = await dialog.ShowAsync();
        await ViewModel.CompleteOnboardingAsync(
            result == ContentDialogResult.Secondary);
    }

    private async void OnAddSoundClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.MusicLibrary
        };
        picker.FileTypeFilter.Add(".wav");
        picker.FileTypeFilter.Add(".mp3");
        picker.FileTypeFilter.Add(".ogg");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
        var files = await picker.PickMultipleFilesAsync();
        if (files.Count == 0)
        {
            return;
        }

        var selectionDescription = files.Count == 1
            ? "the selected sound"
            : $"all {files.Count} selected sounds";
        var modeDialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = files.Count == 1 ? "Add sound" : $"Add {files.Count} sounds",
            Content =
                $"Choose how to add {selectionDescription}. " +
                "Copy keeps private managed copies. Link uses the original files and saves disk space.",
            PrimaryButtonText = "Copy to library",
            SecondaryButtonText = files.Count == 1 ? "Link original" : "Link originals",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };
        var mode = await modeDialog.ShowAsync();
        if (mode == ContentDialogResult.None)
        {
            return;
        }

        foreach (var file in files)
        {
            await ViewModel.ImportSoundAsync(
                file.Path,
                mode == ContentDialogResult.Primary);
        }
    }

    private async void OnDeviceSelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_loaded)
        {
            await ViewModel.SaveDeviceSelectionAsync();
        }
    }

    private async void OnUpdateConsentToggled(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            await ViewModel.SetUpdateConsentAsync(UpdateConsentToggle.IsOn);
        }
    }

    private async void OnAutostartToggled(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            await ViewModel.SetAutostartAsync(AutostartToggle.IsOn);
        }
    }

    private async void OnBufferSelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_loaded)
        {
            await ViewModel.SetBufferProfileAsync(
                BufferProfileSelector.SelectedItem as string);
        }
    }

    private async void OnSetHotkeyClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SoundPadViewModel pad })
        {
            return;
        }

        var input = new TextBox
        {
            Header = "Shortcut",
            PlaceholderText = "Ctrl+Alt+F1",
            Text = pad.Pad.Hotkey ?? string.Empty
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Shortcut for {pad.Name}",
            Content = input,
            PrimaryButtonText = "Save",
            SecondaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.None)
        {
            return;
        }

        var error = await ViewModel.SetHotkeyAsync(
            pad.Pad.Id,
            result == ContentDialogResult.Secondary ? null : input.Text);
        if (error is not null)
        {
            var errorDialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Shortcut unavailable",
                Content = error,
                CloseButtonText = "OK"
            };
            await errorDialog.ShowAsync();
        }
    }
}
