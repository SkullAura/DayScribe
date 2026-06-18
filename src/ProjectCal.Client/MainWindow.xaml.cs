using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using System.IO;
using Windows.UI;
using WinRT.Interop;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace ProjectCal_Client;

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
        ApplyWindowIcon();
        ApplyTitleBarTheme(dark: false);
        RootFrame.Navigate(typeof(MainPage));
    }

    private void ApplyWindowIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (!File.Exists(iconPath))
        {
            return;
        }

        GetAppWindow()?.SetIcon(iconPath);
    }

    public void ApplyTitleBarTheme(bool dark)
    {
        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        var titleBar = GetAppWindow()?.TitleBar;
        if (titleBar is null)
        {
            return;
        }

        var background = dark
            ? Color.FromArgb(255, 20, 22, 20)
            : Color.FromArgb(255, 238, 248, 247);
        var hover = dark
            ? Color.FromArgb(255, 31, 34, 30)
            : Color.FromArgb(255, 247, 252, 251);
        var pressed = dark
            ? Color.FromArgb(255, 38, 42, 37)
            : Color.FromArgb(255, 216, 244, 241);
        var foreground = dark
            ? Color.FromArgb(255, 245, 245, 238)
            : Color.FromArgb(255, 24, 51, 49);
        var inactive = dark
            ? Color.FromArgb(255, 31, 34, 30)
            : Color.FromArgb(255, 247, 252, 251);

        titleBar.BackgroundColor = background;
        titleBar.ForegroundColor = foreground;
        titleBar.InactiveBackgroundColor = inactive;
        titleBar.InactiveForegroundColor = Color.FromArgb(255, 96, 115, 112);
        titleBar.ButtonBackgroundColor = background;
        titleBar.ButtonForegroundColor = foreground;
        titleBar.ButtonHoverBackgroundColor = hover;
        titleBar.ButtonHoverForegroundColor = foreground;
        titleBar.ButtonPressedBackgroundColor = pressed;
        titleBar.ButtonPressedForegroundColor = foreground;
        titleBar.ButtonInactiveBackgroundColor = inactive;
        titleBar.ButtonInactiveForegroundColor = Color.FromArgb(255, 96, 115, 112);
    }

    private AppWindow? GetAppWindow()
    {
        var windowHandle = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(windowHandle);
        return AppWindow.GetFromWindowId(windowId);
    }
}
