// SPDX-License-Identifier: AGPL-3.0-only

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace CouchSubs.App.Theme;

/// <summary>
/// Pencerelerin başlık çubuğunu uygulamanın zeminine boyar. Windows'un mavi başlık çubuğu koyu arayüzün üstünde ayrı
/// bir şerit gibi duruyordu. Windows 11'de çubuk zemin rengini alır; Windows 10'da yalnız koyu/açık moda geçer.
/// </summary>
public static class TitleBar
{
    private const int DarkMode = 20;
    private const int CaptionColor = 35;
    private const int TextColor = 36;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    /// <summary>Uygulamadaki her pencere açılınca başlık çubuğunu boyar.</summary>
    public static void FollowAllWindows()
    {
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
        {
            if (sender is Window window) Paint(window);
        }));
    }

    public static void Paint(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0) return;
        var dark = ThemeManager.IsDarkTheme() ? 1 : 0;
        DwmSetWindowAttribute(handle, DarkMode, ref dark, sizeof(int));
        if (Application.Current.TryFindResource("backgroundColor") is Color background)
        {
            var caption = ColorRef(background);
            DwmSetWindowAttribute(handle, CaptionColor, ref caption, sizeof(int));
        }
        if (Application.Current.TryFindResource("textPrimaryColor") is Color text)
        {
            var color = ColorRef(text);
            DwmSetWindowAttribute(handle, TextColor, ref color, sizeof(int));
        }
    }

    // COLORREF: 0x00BBGGRR
    private static int ColorRef(Color color) => color.R | (color.G << 8) | (color.B << 16);
}
