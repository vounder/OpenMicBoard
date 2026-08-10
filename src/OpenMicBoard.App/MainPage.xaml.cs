using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using OpenMicBoard.App.Services;
using OpenMicBoard.App.ViewModels;
using OpenMicBoard.Core;
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

    private async void OnPlayPadClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: SoundPadViewModel pad })
        {
            await ViewModel.PlayPadAsync(pad);
        }
    }

    private async void OnPreviewPadClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: SoundPadViewModel pad })
        {
            await ViewModel.PreviewPadAsync(pad);
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
            PlaceholderText = "Numpad 1 or Ctrl+Alt+F1",
            Text = pad.Pad.Hotkey ?? string.Empty
        };
        var singleKey = new ComboBox
        {
            Header = "Quick single-key selection",
            PlaceholderText = "Choose Numpad, media, or F13–F24",
            ItemsSource = HotkeyChordParser.SingleKeySuggestions
        };
        singleKey.SelectionChanged += (_, _) =>
        {
            if (singleKey.SelectedItem is string suggestion)
            {
                input.Text = suggestion;
            }
        };
        var warning = new TextBlock
        {
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 224, 179, 84)),
            Text =
                "Single letters, number-row keys, Space, and navigation keys are allowed too. " +
                "Because the shortcut is global, that key may no longer reach games or text fields while OpenMicBoard is running.",
            TextWrapping = TextWrapping.Wrap
        };
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(input);
        content.Children.Add(singleKey);
        content.Children.Add(warning);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Shortcut for {pad.Name}",
            Content = content,
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

    private async void OnAddProfileClick(object sender, RoutedEventArgs e)
    {
        var input = new TextBox { Header = "Profile name", Text = ViewModel.SuggestedProfileName, MaxLength = 80 };
        var error = new TextBlock { Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 205, 92, 92)), TextWrapping = TextWrapping.Wrap };
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(input); content.Children.Add(error);
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "New profile", Content = content, PrimaryButtonText = "Save", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary };
        void Validate()
        {
            var value = input.Text.Trim();
            error.Text = value.Length is < 1 or > 80
                ? "Use 1–80 characters."
                : ViewModel.Profiles.Any(x => string.Equals(x.Name, value, StringComparison.OrdinalIgnoreCase)) ? "That profile name is already used." : string.Empty;
            dialog.IsPrimaryButtonEnabled = string.IsNullOrEmpty(error.Text);
        }
        input.TextChanged += (_, _) => Validate();
        Validate();
        dialog.PrimaryButtonClick += (_, args) =>
        {
            Validate();
            args.Cancel = !dialog.IsPrimaryButtonEnabled;
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            var saveError = await ViewModel.AddProfileAsync(input.Text);
            if (saveError is not null) await ShowErrorAsync("Profile not saved", saveError);
        }
    }

    private async void OnEditPadClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SoundPadViewModel pad }) return;
        var name = new TextBox { Header = "Name", Text = pad.Name, MaxLength = 80 };
        name.SetValue(AutomationProperties.NameProperty, "Sound pad name");
        var validation = new TextBlock { Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 205, 92, 92)) };
        var search = new TextBox { Header = "Symbol search", PlaceholderText = "Name, token, or keyword" };
        search.SetValue(AutomationProperties.NameProperty, "Search pad symbols");
        var icons = new GridView { Height = 155, SelectionMode = ListViewSelectionMode.Single, IsItemClickEnabled = true, ItemsSource = SoundPadIconCatalog.All };
        icons.SetValue(AutomationProperties.NameProperty, "Sound pad symbol choices");
        icons.ItemTemplate = (DataTemplate)Resources["IconPickerTemplate"];
        icons.SelectedItem = SoundPadIconCatalog.Resolve(pad.Pad.IconToken);
        search.TextChanged += (_, _) => icons.ItemsSource = SoundPadIconCatalog.Search(search.Text).ToArray();
        var picker = new ColorPicker { IsAlphaEnabled = false, Color = ViewModels.MainPageViewModel.ParsePadColor(pad.Pad.ColorToken) };
        picker.SetValue(AutomationProperties.NameProperty, "Pad color");
        var previewIcon = new FontIcon { FontSize = 18 };
        var previewName = new TextBlock { FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        var previewStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        previewStack.Children.Add(previewIcon);
        previewStack.Children.Add(previewName);
        var preview = new Border { Height = 48, CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 0, 12, 0), Child = previewStack };
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(name);
        content.Children.Add(validation);
        content.Children.Add(search);
        content.Children.Add(icons);
        content.Children.Add(new TextBlock { Text = "Pad color" });
        content.Children.Add(picker);
        content.Children.Add(preview);
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "Edit sound pad", Content = new ScrollViewer { Content = content, MaxHeight = 620, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, PrimaryButtonText = "Save", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary };
        void UpdatePreview()
        {
            var icon = icons.SelectedItem as SoundPadIcon ?? SoundPadIconCatalog.Resolve(pad.Pad.IconToken);
            var color = picker.Color;
            var foreground = ViewModels.MainPageViewModel.GetContrastingTextColor(color);
            preview.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);
            previewIcon.Glyph = icon.Glyph;
            previewIcon.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(foreground);
            previewName.Text = string.IsNullOrWhiteSpace(name.Text) ? "Unnamed pad" : name.Text;
            previewName.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(foreground);
            validation.Text = name.Text.Trim().Length is < 1 or > 80 ? "Use 1–80 characters." : string.Empty;
            dialog.IsPrimaryButtonEnabled = string.IsNullOrEmpty(validation.Text);
        }
        name.TextChanged += (_, _) => UpdatePreview();
        icons.SelectionChanged += (_, _) => UpdatePreview();
        picker.ColorChanged += (_, _) => UpdatePreview();
        UpdatePreview();
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            var icon = icons.SelectedItem as SoundPadIcon ?? SoundPadIconCatalog.Resolve(pad.Pad.IconToken);
            var hex = $"#{picker.Color.R:X2}{picker.Color.G:X2}{picker.Color.B:X2}";
            var saveError = await ViewModel.EditPadAsync(pad.Pad.Id, name.Text, icon.Token, hex);
            if (saveError is not null) await ShowErrorAsync("Pad not saved", saveError);
        }
    }

    private async void OnRemovePadClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: SoundPadViewModel pad })
        {
            return;
        }

        var message = pad.Asset.Kind == SoundAssetKind.Managed
            ? "Remove this sound from the current profile? If no other profile uses it, " +
              "its private managed copy will also be deleted."
            : "Remove this sound from the current profile? The linked original file will not be deleted.";
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Remove “{pad.Name}”?",
            Content = message,
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var removeError = await ViewModel.RemovePadAsync(pad.Pad.Id);
        if (removeError is not null)
        {
            await ShowErrorAsync("Sound not removed", removeError);
        }
    }

    private async Task ShowErrorAsync(string title, string message) =>
        await new ContentDialog { XamlRoot = XamlRoot, Title = title, Content = message, CloseButtonText = "OK" }.ShowAsync();
}
