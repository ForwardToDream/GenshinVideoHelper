using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GenshinVideoHelper.Core.Models;
using GenshinVideoHelper.Core.Settings;

namespace GenshinVideoHelper.App;

public partial class MainWindow
{
    private HistoryRow? _pendingHistoryDelete;

    private sealed class HistoryRow(FollowLinkEntry entry) : INotifyPropertyChanged
    {
        public string Url => entry.Url;
        public string Detail => $"{entry.Url}\n上次跟随：{entry.FollowedAt.ToLocalTime():yyyy-MM-dd HH:mm}";
        private bool _confirmDelete;
        public string DeleteGlyph => _confirmDelete ? "\uE73E" : "\uE74D";
        public string DeleteHint => _confirmDelete ? "确认删除" : "删除记录";
        public event PropertyChangedEventHandler? PropertyChanged;
        public void ConfirmDelete(bool value)
        {
            _confirmDelete = value;
            PropertyChanged?.Invoke(this, new(nameof(DeleteGlyph)));
            PropertyChanged?.Invoke(this, new(nameof(DeleteHint)));
        }
    }

    private void UpdateHistoryAvailability()
    {
        var manual = _settings.SelectedVideoLibraryId is null;
        if (!manual) LinkHistoryPopup.IsOpen = false;
        LinkHistoryToggle.Visibility = manual ? Visibility.Visible : Visibility.Collapsed;
        UrlInput.Padding = manual ? new Thickness(10, 8, 42, 8) : new Thickness(10, 8, 10, 8);
    }

    private void RememberManualFollow(VideoIdentity identity)
    {
        var previous = _settings.ManualFollowHistory;
        _settings.ManualFollowHistory = FollowLinkHistory.Remember(previous, identity, DateTimeOffset.UtcNow);
        if (!SaveSettings()) _settings.ManualFollowHistory = previous;
        if (LinkHistoryPopup.IsOpen) RefreshLinkHistory();
    }

    private void RefreshLinkHistory()
    {
        _pendingHistoryDelete = null;
        LinkHistoryList.ItemsSource = _settings.ManualFollowHistory.Select(entry => new HistoryRow(entry)).ToArray();
        LinkHistoryEmpty.Visibility = _settings.ManualFollowHistory.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void LinkHistoryPopup_Opened(object sender, EventArgs e) => RefreshLinkHistory();
    private void LinkHistoryPopup_Closed(object sender, EventArgs e)
    {
        _pendingHistoryDelete?.ConfirmDelete(false);
        _pendingHistoryDelete = null;
    }

    private void LinkHistory_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !LinkHistoryPopup.IsOpen) return;
        LinkHistoryPopup.IsOpen = false;
        e.Handled = true;
        UrlInput.Focus();
    }

    private void HistorySelect_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: HistoryRow row }) return;
        LinkHistoryPopup.IsOpen = false;
        UrlInput.Text = row.Url;
        PrepareEpisodePreview(immediate: true);
        UrlInput.Focus();
        UrlInput.CaretIndex = UrlInput.Text.Length;
        e.Handled = true;
    }

    private void HistoryDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: HistoryRow row }) return;
        e.Handled = true;
        if (_pendingHistoryDelete != row)
        {
            _pendingHistoryDelete?.ConfirmDelete(false);
            _pendingHistoryDelete = row;
            row.ConfirmDelete(true);
            return;
        }
        var previous = _settings.ManualFollowHistory;
        _settings.ManualFollowHistory = previous.Where(entry => entry.Url != row.Url).ToList();
        if (!SaveSettings()) { _settings.ManualFollowHistory = previous; return; }
        RefreshLinkHistory();
    }
}
