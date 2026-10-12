using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GenshinVideoHelper.App.Native;
using GenshinVideoHelper.Core.Models;
using GenshinVideoHelper.Core.Application;
using GenshinVideoHelper.Infrastructure.Browser;
using GenshinVideoHelper.Infrastructure.Library;
using GenshinVideoHelper.Infrastructure.Settings;
using GenshinVideoHelper.App.Composition;
using GenshinVideoHelper.Core.Settings;
using GenshinVideoHelper.Core.Library;

namespace GenshinVideoHelper.Smoke;

internal static class UiHarness
{
    public static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T item) yield return item;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    public static void Capture(Window window, string root, string name, int width, int height)
    {
        var content = (FrameworkElement)window.Content;
        var scale = content.LayoutTransform.Value.M11;
        var size = new Size(width * scale, height * scale);
        window.Width = size.Width;
        window.Height = size.Height;
        content.Measure(size);
        content.Arrange(new Rect(size));
        content.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        if (window is GenshinVideoHelper.App.MainWindow main && ((ScrollViewer)main.FindName("FollowPage")).Visibility == Visibility.Visible)
        {
            var start = (Button)main.FindName("OpenButton");
            var url = (TextBox)main.FindName("UrlInput");
            var episodes = (ComboBox)main.FindName("EpisodeSelector");
            Check(Math.Abs(start.ActualWidth - url.ActualWidth) < 1 && start.TranslatePoint(new Point(), content).Y >= episodes.TranslatePoint(new Point(0, episodes.ActualHeight), content).Y + 11, "Start spans full card width on its own row beneath episodes");
        }
        var dpi = VisualTreeHelper.GetDpi(content);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width * dpi.DpiScaleX),
            (int)Math.Ceiling(size.Height * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(root, "artifacts", name));
        encoder.Save(file);
    }

    public static object? Invoke(object instance, string method, params object[] args) =>
        instance.GetType().GetMethod(method, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(instance, args);
    public static T? GetField<T>(object instance, string field) =>
        (T?)instance.GetType().GetField(field, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(instance);
    public static void CaptureEpisodeMenu(GenshinVideoHelper.App.MainWindow window, string root, string name) => CaptureMenu(window, (ComboBox)window.FindName("EpisodeSelector"), root, name);

    public static void CaptureMenu(GenshinVideoHelper.App.MainWindow window, ComboBox combo, string root, string name)
    {
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
        combo.IsDropDownOpen = true;
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
        var popup = (Popup)combo.Template.FindName("PART_Popup", combo);
        var child = (FrameworkElement)popup.Child;
        child.UpdateLayout();
        Check(child.ActualWidth > 0 && child.ActualWidth <= combo.ActualWidth + 1 && child.ActualHeight <= 300,
            "Dropdown stays within control width and scrolls at 300 reference units");
        var first = child.PointToScreen(new Point());
        var last = child.PointToScreen(new Point(child.ActualWidth, child.ActualHeight));
        var controlWidth = combo.PointToScreen(new Point(combo.ActualWidth, 0)).X - combo.PointToScreen(new Point()).X;
        Check(Math.Abs(last.X - first.X - controlWidth) <= 2, "Dropdown physical width matches the control without double scaling");
        var dpi = VisualTreeHelper.GetDpi(child);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(last.X - first.X), (int)Math.Ceiling(last.Y - first.Y),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(child);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(Path.Combine(root, "artifacts", name))) encoder.Save(file);
        combo.IsDropDownOpen = false;
    }

    public static Application CreateTestApplication()
    {
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("pack://application:,,,/GenshinVideoHelper;component/Resources/PclTheme.xaml") });
        return app;
    }

}
