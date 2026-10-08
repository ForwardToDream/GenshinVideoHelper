using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using GenshinVideoHelper.Core.Library;

namespace GenshinVideoHelper.App;

public partial class LibraryPickerWindow : Window
{
    private readonly Choice[] _choices;
    private readonly string? _currentId;
    public VideoLibrary? SelectedLibrary { get; private set; }

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
        Filter();
        if (catalog.Errors.Count > 0) PickerStatusText.Text = "部分视频库未能加载，可使用其他库或手动输入。";
        Loaded += (_, _) => SearchInput.Focus();
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        // XAML can raise TextChanged before the result list is initialized.
        if (LibraryResults is not null) Filter();
    }

    private void Filter()
    {
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
