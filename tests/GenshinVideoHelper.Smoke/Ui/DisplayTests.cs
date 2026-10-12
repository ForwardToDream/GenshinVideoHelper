using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GenshinVideoHelper.App;
using GenshinVideoHelper.App.Composition;
using GenshinVideoHelper.App.Native;
using GenshinVideoHelper.Core.Settings;

namespace GenshinVideoHelper.Smoke;

internal static class DisplayTests
{
    public static void Run(string root)
    {
        var app = CreateTestApplication();
        var directory = Path.Combine(root, "artifacts", "display-layout");
        Directory.CreateDirectory(directory);
        var window = new MainWindow(new AppServices(new AppSettings { IsFirstLaunch = false, HotkeysEnabled = false },
            TestArtifacts.PathFor(root, "display-ui"), episodeFactory: PreviewService));
        var content = (FrameworkElement)window.Content;
        try
        {
            foreach (var (width, height) in new[] { (1280, 720), (1920, 1080), (2560, 1440), (3840, 2160) })
            foreach (var dpi in new uint[] { 96, 144, 192, 288 })
            {
                var display = new DisplayMetrics(new(0, 0, width, height), new(0, 0, width, height - 40), dpi, "synthetic");
                window.DisplayLayout.Apply(display);
                var availableWidth = display.WorkArea.Width / display.DpiScale;
                var availableHeight = display.WorkArea.Height / display.DpiScale;
                Check(Math.Abs(window.Width - Math.Min(1020, availableWidth)) < .001,
                    $"{width}x{height}@{dpi}: main window uses DIPs and fits the work area");
                Check(Math.Abs(window.Height - Math.Min(730, availableHeight)) < .001,
                    "High zoom constrains the window instead of shrinking its controls");
                Check(window.MinWidth <= window.Width && window.MinHeight <= window.Height, "Small work areas can override minimum outer size");
                var name = $"{width}x{height}-{dpi * 100 / 96}";
                foreach (var page in new[] { "Follow", "Settings", "Hotkeys", "Progress", "Vision" })
                {
                    ((RadioButton)window.FindName("Nav" + page)).IsChecked = true;
                    Render(window, display, Path.Combine(directory, name + "-" + page + ".png"));
                    Check(content.LayoutTransform.Value.IsIdentity, "WPF owns DPI scaling without a compensating transform");
                    Check(((TextBox)window.FindName("UrlInput")).FontSize == 13, "Logical text size respects system zoom");
                    Check(window.Width * display.DpiScale <= width && window.Height * display.DpiScale <= display.WorkArea.Height,
                        "Window remains within the physical work area");
                    window.Width = window.MinWidth;
                    window.Height = window.MinHeight;
                    Render(window, display, Path.Combine(directory, name + "-" + page + "-compact.png"));
                    var start = (Button)window.FindName("OpenButton");
                    if (page == "Follow")
                    {
                        Check(Math.Abs(start.ActualWidth - ((TextBox)window.FindName("UrlInput")).ActualWidth) < 1,
                            "Start retains full card width at minimum size");
                        var scroll = (ScrollViewer)window.FindName("FollowPage");
                        if (availableHeight < 600)
                            Check(scroll.ScrollableHeight > 0 && scroll.ViewportHeight > 0,
                                "Large system text leaves lower controls reachable through vertical scrolling");
                    }
                    window.Width = Math.Min(1020, availableWidth);
                    window.Height = Math.Min(730, availableHeight);
                }
                var combo = (ComboBox)window.FindName("BrowserSelector");
                combo.ApplyTemplate();
                var popup = (Popup)combo.Template.FindName("PART_Popup", combo);
                var popupContent = (FrameworkElement)popup.Child;
                Check(popupContent.LayoutTransform.Value.IsIdentity,
                    "Dropdown lets WPF inherit the parent scale without applying it twice");
            }
            var library = new LibraryPickerWindow(window.Services.Libraries, window.Services.Settings.SelectedVideoLibraryId);
            var login = new LoginPromptWindow();
            var pip = new PipFrameWindow();
            var native = DisplayMetrics.ForWindow(pip.WindowHandle);
            Check(((FrameworkElement)pip.Content).LayoutTransform.Value.IsIdentity, "PiP controls retain their DIP sizes");
            Console.WriteLine($"Current desktop: {native.Monitor.Width}x{native.Monitor.Height}, DPI {native.Dpi}.");
            foreach (var dialog in new Window[] { library, login })
            {
                var layout = dialog is LibraryPickerWindow picker ? picker.DisplayLayout : ((LoginPromptWindow)dialog).DisplayLayout;
                foreach (var (width, height, dpi) in new[] { (3840, 2160, 144u), (1920, 1080, 288u), (1280, 720, 288u) })
                {
                    var display = new DisplayMetrics(new(0, 0, width, height), new(0, 0, width, height - 40), dpi, "synthetic");
                    layout.Apply(display);
                    Render(dialog, display, Path.Combine(directory, $"{dialog.GetType().Name}-{width}x{height}-{dpi * 100 / 96}.png"));
                    Check(((FrameworkElement)dialog.Content).LayoutTransform.Value.IsIdentity,
                        "Dialogs use WPF DPI scaling without a second transform");
                    Check(dialog.Width * display.DpiScale <= width && dialog.Height * display.DpiScale <= display.WorkArea.Height,
                        "Dialogs remain within the work area with large system text");
                    if (dialog is LibraryPickerWindow && dialog.Height < 380)
                    {
                        var scroll = (ScrollViewer)dialog.FindName("PickerViewport");
                        Check(scroll.ScrollableHeight > 0 && scroll.ViewportHeight > 0,
                            "Library search, results and confirmation remain reachable by scrolling");
                        scroll.ScrollToEnd();
                        dialog.UpdateLayout();
                        Check(scroll.VerticalOffset > 0, "Library confirmation can be scrolled into view");
                        scroll.ScrollToTop();
                    }
                }
                dialog.Close();
            }
            pip.Close();
            TestPopupScaling();
            TestWorkAreaRecovery();
            Console.WriteLine("Display render matrix: 4 resolutions x 4 DPI settings x 5 pages x default/minimum size; dropdown, dialogs, PiP bar and work-area recovery passed. Synthetic DPI; desktop settings unchanged.");
        }
        finally { window.Close(); app.Shutdown(); }
    }

    private static void TestPopupScaling()
    {
        // Actual popup HWNDs at several logical control widths on the current desktop.
        foreach (var width in new[] { 220, 420, 640 })
        {
            var combo = new ComboBox { ItemsSource = new[] { "自动", "Chrome · 安装路径", "Edge · 安装路径" }, SelectedIndex = 0 };
            var content = new Grid();
            content.Children.Add(combo);
            var shell = new Window { Content = content, Width = width, Height = 40,
                WindowStyle = WindowStyle.None, Left = -10000, Top = -10000, ShowInTaskbar = false, ShowActivated = false };
            try
            {
                shell.Show();
                combo.IsDropDownOpen = true;
                shell.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
                var popup = (Popup)combo.Template.FindName("PART_Popup", combo);
                var child = (FrameworkElement)popup.Child;
                var actual = child.PointToScreen(new Point(child.ActualWidth, 0)).X - child.PointToScreen(new Point()).X;
                var expected = combo.PointToScreen(new Point(combo.ActualWidth, 0)).X - combo.PointToScreen(new Point()).X;
                Check(Math.Abs(actual - expected) <= 2, $"Popup: physical width {actual} matches input {expected}");
                combo.IsDropDownOpen = false;

                var tip = new ToolTip { Content = "系统缩放提示", PlacementTarget = combo };
                combo.ToolTip = tip;
                tip.IsOpen = true;
                shell.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
                actual = tip.PointToScreen(new Point(tip.ActualWidth, 0)).X - tip.PointToScreen(new Point()).X;
                expected = tip.ActualWidth * VisualTreeHelper.GetDpi(tip).DpiScaleX;
                Check(Math.Abs(actual - expected) <= 2 && tip.LayoutTransform.Value.IsIdentity, "Tooltip uses WPF DPI scaling once");
                tip.IsOpen = false;
            }
            finally { shell.Close(); }
        }
    }

    private static void TestWorkAreaRecovery()
    {
        var shell = new Window { Width = 1020, Height = 730, MinWidth = 860, MinHeight = 600, Content = new Grid() };
        var layout = new WindowDisplayLayout(shell);
        try
        {
            var large = new DisplayMetrics(new(0, 0, 3840, 2160), new(0, 0, 3840, 2080), 192, "test");
            layout.Apply(large);
            Check(shell.Width == 1020 && shell.Height == 730, "DIP preference on large work area");
            layout.Apply(new(new(0, 0, 1280, 720), new(0, 0, 1280, 672), 288, "test"));
            Check(shell.Width < 860 && shell.Height < 600, "Temporary tiny logical work area clamps the outer window");
            layout.Apply(large);
            Check(shell.Width == 1020 && shell.Height == 730, "Returning to a large display restores the preferred DIP size");
            var nested = false;
            layout.DisplayChanged += (_, current) =>
            {
                Check(layout.IsApplying, "Display callbacks run inside the automatic adjustment scope");
                if (nested) return;
                nested = true;
                layout.Apply(current with { Dpi = 144 });
                Check(layout.IsApplying, "Nested monitor/DPI updates keep automatic adjustment isolation");
            };
            layout.Apply(large with { WorkArea = new(0, 0, 3840, 2040) });
            Check(!layout.IsApplying, "Automatic adjustment scope is released after all callbacks");
        }
        finally { shell.Close(); }
    }

    private static void Render(Window window, DisplayMetrics display, string path)
    {
        var content = (FrameworkElement)window.Content;
        var size = new Size(window.Width, window.Height);
        content.Measure(size);
        content.Arrange(new Rect(size));
        content.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width * display.DpiScale),
            (int)Math.Ceiling(size.Height * display.DpiScale), display.Dpi, display.Dpi, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
