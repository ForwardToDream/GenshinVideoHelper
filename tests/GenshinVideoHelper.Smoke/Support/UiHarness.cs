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
        window.Width = width;
        window.Height = height;
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));
        content.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        if (window is GenshinVideoHelper.App.MainWindow main && ((ScrollViewer)main.FindName("FollowPage")).Visibility == Visibility.Visible)
        {
            var start = (Button)main.FindName("OpenButton");
            var url = (TextBox)main.FindName("UrlInput");
            var episodes = (ComboBox)main.FindName("EpisodeSelector");
            Check(Math.Abs(start.ActualWidth - url.ActualWidth) < 1 && start.TranslatePoint(new Point(), content).Y >= episodes.TranslatePoint(new Point(0, episodes.ActualHeight), content).Y + 11, "Start spans full card width on its own row beneath episodes");
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
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
            "Episode dropdown stays within control width and scrolls at 300px");
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(child.ActualWidth), (int)Math.Ceiling(child.ActualHeight), 96, 96, PixelFormats.Pbgra32);
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
