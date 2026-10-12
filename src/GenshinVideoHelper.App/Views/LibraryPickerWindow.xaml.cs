using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using GenshinVideoHelper.Core.Library;

namespace GenshinVideoHelper.App;

public partial class LibraryPickerWindow : Window
{
    private readonly Choice[] _choices;
    private readonly string? _currentId;
    private ScrollViewer? _wheelViewer;
    private double _wheelTarget;
    private int _wheelVersion;
    private static readonly DependencyProperty WheelOffsetProperty = DependencyProperty.Register(
        "WheelOffset", typeof(double), typeof(LibraryPickerWindow), new PropertyMetadata(0d, (sender, args) =>
            ((LibraryPickerWindow)sender)._wheelViewer?.ScrollToVerticalOffset((double)args.NewValue)));
    public VideoLibrary? SelectedLibrary { get; private set; }
    public Native.WindowDisplayLayout DisplayLayout { get; }

    private sealed record Choice(VideoLibrary? Library)
    {
        public string DisplayName => Library?.Name ?? "手动输入";
        public string CreatorText => $"UP 主：{Library?.CreatorName} ↗";
        public Visibility CreatorVisibility => Library is null ? Visibility.Collapsed : Visibility.Visible;
        public string CountText => Library is null ? "" : $"{Library.Videos.Count} 个视频";
        public string Description => Library?.Description ?? "不使用预设库，保留地址框自行输入视频链接。";

        public bool Matches(IEnumerable<string> words)
        {
            var fields = Library is null ? new[] { DisplayName, Description } :
                new[] { Library.Id, Library.Name, Library.CreatorName, Library.Description }.Concat(Library.Videos.SelectMany(video => new[] { video.Title, video.Bvid }));
            return words.All(word => fields.Any(field => field.Contains(word, StringComparison.OrdinalIgnoreCase)));
        }
    }

    public LibraryPickerWindow(VideoLibraryCatalog catalog, string? currentId)
    {
        _currentId = currentId;
        _choices = new[] { new Choice(null) }.Concat(catalog.Libraries.Select(library => new Choice(library))).ToArray();
        InitializeComponent();
        DisplayLayout = new(this);
        Filter();
        if (catalog.Errors.Count > 0) PickerStatusText.Text = "部分视频库未能加载，可使用其他库或手动输入。";
        Loaded += (_, _) => SearchInput.Focus();
        Closed += (_, _) => StopScrolling();
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        // XAML can raise TextChanged before the result list is initialized.
        if (LibraryResults is not null) Filter();
    }

    private void Filter()
    {
        StopScrolling();
        var previous = LibraryResults.SelectedItem as Choice;
        var words = SearchInput.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var results = _choices.Where(choice => choice.Matches(words)).ToArray();
        LibraryResults.ItemsSource = results;
        LibraryResults.SelectedItem = previous is not null && results.Contains(previous) ? previous :
            results.FirstOrDefault(choice => choice.Library?.Id == _currentId);
        EmptyStateText.Visibility = results.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ResultCountText.Text = $"{results.Count(choice => choice.Library is not null)} 个视频库";
        UseLibraryButton.IsEnabled = LibraryResults.SelectedItem is Choice;
    }

    private void Picker_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta == 0) return;
        var source = e.OriginalSource;
        var insideList = source == LibraryResults || (source is Visual visual && LibraryResults.IsAncestorOf(visual));
        if (insideList || (source is ContentElement && LibraryResults.IsMouseOver))
        {
            LibraryResults.ApplyTemplate();
            var listScroll = (ScrollViewer)LibraryResults.Template.FindName("LibraryScroll", LibraryResults);
            if (ScrollWheel(listScroll, e.Delta, SystemParameters.ClientAreaAnimation)) { e.Handled = true; return; }
        }
        e.Handled = ScrollWheel(PickerViewport, e.Delta, SystemParameters.ClientAreaAnimation);
    }

    private bool ScrollWheel(ScrollViewer viewer, int delta, bool animate)
    {
        if (viewer.ScrollableHeight <= 0) return false;
        var current = viewer.VerticalOffset;
        var distance = -delta / 120d * 40;
        // Repeated input accumulates; reversing direction starts from the visible position.
        var continuing = _wheelViewer == viewer && Math.Sign(_wheelTarget - current) == Math.Sign(distance);
        var target = Math.Clamp((continuing ? _wheelTarget : current) + distance, 0, viewer.ScrollableHeight);
        if (Math.Abs(target - current) < .01)
        {
            if (_wheelViewer == viewer) StopScrolling();
            return false;
        }
        StopScrolling();
        _wheelViewer = viewer;
        _wheelTarget = target;
        SetValue(WheelOffsetProperty, current);
        var version = ++_wheelVersion;
        if (!animate)
        {
            viewer.ScrollToVerticalOffset(target);
            // Keep the pending target until layout applies it, so rapid input still accumulates.
            Dispatcher.BeginInvoke(() => { if (version == _wheelVersion) StopScrolling(); }, DispatcherPriority.Loaded);
            return true;
        }
        var animation = new DoubleAnimation(current, target, TimeSpan.FromMilliseconds(160))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        animation.Completed += (_, _) =>
        {
            if (version != _wheelVersion) return;
            StopScrolling();
            viewer.ScrollToVerticalOffset(target);
        };
        BeginAnimation(WheelOffsetProperty, animation, HandoffBehavior.SnapshotAndReplace);
        return true;
    }

    private void StopWheelScrolling(object sender, RoutedEventArgs e) => StopScrolling();
    private void StopScrolling()
    {
        if (_wheelViewer is null) return;
        var viewer = _wheelViewer;
        var offset = viewer.VerticalOffset;
        _wheelViewer = null;
        _wheelVersion++;
        BeginAnimation(WheelOffsetProperty, null);
        SetValue(WheelOffsetProperty, offset);
        viewer.ScrollToVerticalOffset(offset);
    }

    private void LibraryResults_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (UseLibraryButton is not null)
        {
            UseLibraryButton.IsEnabled = LibraryResults.SelectedItem is Choice;
            UseLibraryButton.Content = LibraryResults.SelectedItem is Choice { Library: null } ? "手动输入" : "使用此库";
        }
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e) { SearchInput.Clear(); SearchInput.Focus(); }
    private void UseLibrary_Click(object sender, RoutedEventArgs e)
    {
        if (LibraryResults.SelectedItem is not Choice choice) return;
        SelectedLibrary = choice.Library;
        DialogResult = true;
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Creator_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string url }) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        { PickerStatusText.Text = $"无法打开 UP 主空间：{ex.Message}"; }
    }
}
