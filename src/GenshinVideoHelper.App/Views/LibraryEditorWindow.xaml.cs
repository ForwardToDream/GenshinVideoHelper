using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using GenshinVideoHelper.Core.Library;
using GenshinVideoHelper.Core.Models;

namespace GenshinVideoHelper.App;

public partial class LibraryEditorWindow : Window
{
    private readonly IVideoLibraryStore _store;
    private readonly VideoLibrary? _original;
    private bool _saving;
    public ObservableCollection<VideoRow> Rows { get; } = [];
    public VideoLibrary? SavedLibrary { get; private set; }
    public Native.WindowDisplayLayout DisplayLayout { get; }

    public sealed class VideoRow
    {
        public string Number { get; set; } = "";
        public string Title { get; set; } = "";
        public string Url { get; set; } = "";
    }

    public LibraryEditorWindow(IVideoLibraryStore store, VideoLibrary? library = null)
    {
        _store = store;
        _original = library;
        InitializeComponent();
        Title = EditorHeading.Text = library is null ? "新增视频库" : "编辑视频库";
        LibraryIdInput.Text = library?.Id ?? "library-" + Guid.NewGuid().ToString("N");
        LibraryIdInput.IsReadOnly = library is not null;
        LibraryNameInput.Text = library?.Name ?? "";
        CreatorNameInput.Text = library?.CreatorName ?? "";
        CreatorInput.Text = library is null ? "" : new Uri(library.CreatorUrl).AbsolutePath.Trim('/');
        DescriptionInput.Text = library?.Description ?? "";
        foreach (var video in library?.Videos ?? [])
            Rows.Add(new() { Number = video.Number.ToString(CultureInfo.InvariantCulture), Title = video.Title, Url = video.Url });
        if (Rows.Count == 0) AddVideo();
        VideoRows.ItemsSource = Rows;
        DisplayLayout = new(this);
        Loaded += (_, _) => LibraryNameInput.Focus();
    }

    private void AddVideo()
    {
        var next = Rows.Select(row => int.TryParse(row.Number, out var number) ? number : 0).DefaultIfEmpty().Max() + 1;
        Rows.Add(new() { Number = next.ToString(CultureInfo.InvariantCulture) });
    }

    private void AddVideo_Click(object sender, RoutedEventArgs e)
    {
        AddVideo();
        VideoRows.UpdateLayout();
        AddVideoButton.BringIntoView();
    }
    private void RemoveVideo_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: VideoRow row }) Rows.Remove(row);
    }

    private VideoLibrary ReadLibrary()
    {
        var creator = CreatorInput.Text.Trim();
        if (Regex.IsMatch(creator, "^[0-9]+$")) creator = $"https://space.bilibili.com/{creator}/";
        var videos = Rows.Select((row, index) =>
        {
            if (!int.TryParse(row.Number, out var number) || number <= 0)
                throw new ArgumentException($"第 {index + 1} 行的序号需要是正整数。");
            if (!VideoIdentity.TryParse(row.Url, out var identity))
                throw new ArgumentException($"第 {index + 1} 行的视频链接 / BV 号无效。");
            return new LibraryVideo(number, row.Title, identity!.Bvid);
        }).ToArray();
        return new VideoLibrary(_original?.Id ?? LibraryIdInput.Text.Trim(), LibraryNameInput.Text, CreatorNameInput.Text,
            creator, DescriptionInput.Text, videos).Normalize();
    }

    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveAsync();

    private async Task SaveAsync()
    {
        if (_saving) return;
        try
        {
            var library = ReadLibrary();
            _saving = true;
            EditorFields.IsEnabled = SaveLibraryButton.IsEnabled = CancelEditButton.IsEnabled = false;
            EditorStatusText.Visibility = Visibility.Collapsed;
            await Task.Run(() => _store.Save(library, overwrite: _original is not null));
            SavedLibrary = library;
            _saving = false;
            DialogResult = true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            EditorStatusText.Text = $"未保存：{ex.Message}";
            EditorStatusText.Visibility = Visibility.Visible;
        }
        finally
        {
            _saving = false;
            EditorFields.IsEnabled = SaveLibraryButton.IsEnabled = CancelEditButton.IsEnabled = true;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_saving) e.Cancel = true;
        base.OnClosing(e);
    }

    private void Close_Click(object sender, RoutedEventArgs e) { if (!_saving) Close(); }
}
