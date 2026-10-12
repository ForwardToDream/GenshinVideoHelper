using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using GenshinVideoHelper.App;
using GenshinVideoHelper.Core.Library;

namespace GenshinVideoHelper.Smoke;

internal static class LibraryPickerScrollTests
{
    public static void Run(string root, VideoLibrary library)
    {
        var dispatcher = Application.Current.Dispatcher;
        var task = dispatcher.InvokeAsync(() => RunAsync(root, library)).Task.Unwrap();
        var frame = new DispatcherFrame();
        task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    private static async Task RunAsync(string root, VideoLibrary library)
    {
        var many = Enumerable.Range(0, 80).Select(index => library with
            { Id = $"scroll-{index}", Name = $"滚动测试 {index:00}" }).ToArray();
        var picker = new LibraryPickerWindow(new(many, []), null)
            { ShowActivated = false, ShowInTaskbar = false };
        try
        {
            picker.Show();
            picker.UpdateLayout();
            var results = (ListBox)picker.FindName("LibraryResults");
            var scroll = (ScrollViewer)results.Template.FindName("LibraryScroll", results);
            var samples = new List<double>();
            scroll.ScrollChanged += (_, _) => samples.Add(scroll.VerticalOffset);
            void Wheel(int delta) => results.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
                { RoutedEvent = UIElement.PreviewMouseWheelEvent });
            async Task Settle() { await Task.Delay(300); picker.UpdateLayout(); }

            Check(scroll.ScrollableHeight > 100 && scroll.VerticalOffset == 0, "Long library list has a pixel viewport");
            Check(Descendants<ListBoxItem>(results).Count() < results.Items.Count,
                "Long list retains virtualization instead of constructing every row");
            Wheel(-120);
            Wheel(120);
            await Settle();
            Check(scroll.VerticalOffset == 0, "Reversing before the first frame cancels forward scrolling at the top boundary");
            Wheel(-120);
            await Settle();
            Check(Math.Abs(scroll.VerticalOffset - 40) <= 1, "One wheel notch moves a small pixel distance instead of several cards");
            if (SystemParameters.ClientAreaAnimation)
                Check(samples.Any(offset => offset > 0 && offset < 39), "Wheel movement produces intermediate animation frames");
            Check(!picker.HasAnimatedProperties, "Finished scrolling releases its animation clock");

            Wheel(-120);
            Wheel(-120);
            await Settle();
            Check(Math.Abs(scroll.VerticalOffset - 120) <= 1, "Rapid wheel input accumulates its distance");
            Wheel(-120);
            picker.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            var reverseFrom = scroll.VerticalOffset;
            Wheel(120);
            await Settle();
            Check(Math.Abs(scroll.VerticalOffset - Math.Max(0, reverseFrom - 40)) <= 1,
                "Reverse wheel input cancels forward momentum immediately");

            Wheel(-120);
            ((TextBox)picker.FindName("SearchInput")).Text = "scroll-0";
            await Settle();
            Check(results.Items.Count == 1 && scroll.VerticalOffset == 0 && !picker.HasAnimatedProperties,
                "Filtering cancels old scrolling and leaves the short result stationary");
            ((TextBox)picker.FindName("SearchInput")).Clear();
            picker.UpdateLayout();
            Wheel(-120);
            picker.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(picker)!, 0, Key.Home)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Check(!picker.HasAnimatedProperties, "Keyboard interaction releases wheel animation before navigation");

            scroll.ScrollToTop();
            await Settle();
            Wheel(-30);
            await Settle();
            Check(Math.Abs(scroll.VerticalOffset - 10) <= 1, "Partial wheel input keeps its precision instead of becoming a whole notch");
            Wheel(-120);
            results.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = UIElement.PreviewMouseDownEvent });
            Check(!picker.HasAnimatedProperties, "Mouse interaction cancels wheel animation before scrollbar dragging");

            scroll.ScrollToTop();
            await Settle();
            Invoke(picker, "ScrollWheel", scroll, -120, false);
            Invoke(picker, "ScrollWheel", scroll, -120, false);
            await Settle();
            Check(Math.Abs(scroll.VerticalOffset - 80) <= 1 && !picker.HasAnimatedProperties,
                "Animation-free scrolling still accumulates input before layout");
            scroll.ScrollToTop();
            await Settle();
            Invoke(picker, "ScrollWheel", scroll, -120, false);
            Invoke(picker, "ScrollWheel", scroll, 120, false);
            await Settle();
            Check(scroll.VerticalOffset == 0, "Immediate reverse input cancels a queued nonanimated scroll");

            scroll.ScrollToBottom();
            await Settle();
            var bottom = scroll.VerticalOffset;
            Wheel(-120);
            await Settle();
            Check(scroll.VerticalOffset == bottom, "Scrolling stops at the bottom boundary");
            Capture(picker, root, "ui-library-picker-scroll-bottom.png", 570, 680);
            Wheel(120);
            picker.Close();
            Check(!picker.HasAnimatedProperties, "Closing the picker stops active scrolling");
            Console.WriteLine("Library scrolling: small pixel steps, animation, repeated/reverse input, virtualization, filtering, keyboard and close passed.");
        }
        finally { picker.Close(); }
    }
}
