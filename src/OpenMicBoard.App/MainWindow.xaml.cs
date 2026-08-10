using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;
using Windows.Graphics;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace OpenMicBoard.App;

/// <summary>
/// The application window. This hosts a Frame that displays pages. Add your
/// UI and logic to MainPage.xaml / MainPage.xaml.cs instead of here so you
/// can use Page features such as navigation events and the Loaded lifecycle.
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.SetIcon(Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "AppIcon-v2.ico"));
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var work = area.WorkArea;
        var availableWidth = Math.Max(1, work.Width - 32);
        var availableHeight = Math.Max(1, work.Height - 32);
        var width = Math.Min(1540, availableWidth);
        var height = Math.Min(980, availableHeight);
        AppWindow.Resize(new SizeInt32(width, height));
        AppWindow.Move(new PointInt32(
            work.X + (work.Width - width) / 2,
            work.Y + (work.Height - height) / 2));

        // Navigate the root frame to the main page on startup.
        RootFrame.Navigate(typeof(MainPage));
        Closed += OnClosed;
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        if (RootFrame.Content is MainPage page)
        {
            _ = page.ViewModel.DisposeAsync();
        }
    }
}
