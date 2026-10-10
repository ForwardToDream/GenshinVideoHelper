using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using GenshinVideoHelper.Core.Settings;
using GenshinVideoHelper.Core.Diagnostics;

namespace GenshinVideoHelper.App;

public partial class MainWindow
{
    private TextBox? _capturingBox;
    private bool _editingBindings;

    private sealed class ShortcutRow(HotkeyAction action, string keys)
    {
        public HotkeyAction Action { get; } = action;
        public string Title => HotkeyBindings.Title(Action);
        public string Description => Action switch
        {
            HotkeyAction.SeekBackward or HotkeyAction.SeekForward => "按设置中的步长跳转",
            HotkeyAction.PreviousEpisode or HotkeyAction.NextEpisode => "当前标签页换 P，自动继续跟随",
            HotkeyAction.TogglePip => "开启或关闭浏览器浮窗",
            HotkeyAction.PlacePip => "同时应用设置的浮窗宽度",
            HotkeyAction.ToggleMute => "仅影响当前视频",
            HotkeyAction.ReversePipVisibility => "按一下切换：锁定显示 → 强制隐藏 → 鼠标避让",
            _ => "控制当前选中的视频"
        };
        public string Keys { get; set; } = keys;
    }

    private void PopulateBindings(IReadOnlyDictionary<HotkeyAction, string> bindings)
    {
        HotkeyRows.ItemsSource = HotkeyBindings.Defaults().Select(pair => new ShortcutRow(pair.Key, bindings.GetValueOrDefault(pair.Key, pair.Value))).ToArray();
        UpdateHotkeyTooltips();
    }

    private void UpdateHotkeyTooltips()
    {
        BackButton.ToolTip = _settings.Hotkeys[HotkeyAction.SeekBackward];
        PlayButton.ToolTip = _settings.Hotkeys[HotkeyAction.TogglePlayback];
        ForwardButton.ToolTip = _settings.Hotkeys[HotkeyAction.SeekForward];
        PreviousEpisodeButton.ToolTip = _settings.Hotkeys[HotkeyAction.PreviousEpisode];
        NextEpisodeButton.ToolTip = _settings.Hotkeys[HotkeyAction.NextEpisode];
    }

    private void Binding_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _editingBindings = true;
        _hotkeys?.Disable();
        HotkeyStatus.Text = "正在编辑绑定，全局快捷键暂时停用。离开输入框后恢复已保存的绑定。";
    }

    private void Binding_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender == _capturingBox) { _capturingBox!.IsReadOnly = false; _capturingBox = null; }
        _editingBindings = false;
        ApplyHotkeys();
    }

    private void CaptureBinding_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || VisualTreeHelper.GetParent(button) is not Grid row) return;
        var box = row.Children.OfType<TextBox>().Single();
        if (_capturingBox is not null) _capturingBox.IsReadOnly = false;
        _capturingBox = box;
        box.IsReadOnly = true;
        box.Focus();
        BindingStatus.Text = $"请按下“{((ShortcutRow)box.DataContext).Title}”的新按键或组合键；Esc 取消。";
    }

    private void Binding_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender != _capturingBox) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            _capturingBox!.IsReadOnly = false;
            _capturingBox = null;
            BindingStatus.Text = "已取消录入，原绑定未变更。";
            return;
        }
        if (key is Key.LeftAlt or Key.RightAlt or Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        var modifiers = Keyboard.Modifiers;
        var parts = new List<string>();
        if ((modifiers & ModifierKeys.Control) != 0) parts.Add("Ctrl");
        if ((modifiers & ModifierKeys.Alt) != 0) parts.Add("Alt");
        if ((modifiers & ModifierKeys.Shift) != 0) parts.Add("Shift");
        if ((modifiers & ModifierKeys.Windows) != 0) parts.Add("Win");
        var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        var keyName = virtualKey == 0xC0 ? "~" : virtualKey is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A ? ((char)virtualKey).ToString() : key switch { Key.Return => "Enter", Key.Back => "Backspace", Key.Prior => "PageUp", Key.Next => "PageDown", _ => key.ToString() };
        parts.Add(keyName);
        if (!HotkeyGesture.TryParse(string.Join('+', parts), out var gesture, allowUnmodified: ((ShortcutRow)_capturingBox!.DataContext).Action == HotkeyAction.ReversePipVisibility))
        { BindingStatus.Text = "此按键不受支持。显隐模式项可用单键或组合键，其他项需要修饰键加按键。"; return; }
        _capturingBox!.SetCurrentValue(TextBox.TextProperty, gesture.Text);
        _capturingBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        _capturingBox.IsReadOnly = false;
        _capturingBox = null;
        BindingStatus.Text = "已录入，点击“保存绑定”后生效。";
    }

    private void SaveBindings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var next = HotkeyBindings.Validate(((IEnumerable<ShortcutRow>)HotkeyRows.ItemsSource).ToDictionary(row => row.Action, row => row.Keys));
            var previous = _settings.Hotkeys;
            _settings.Hotkeys = next;
            if (!SaveSettings()) { _settings.Hotkeys = previous; BindingStatus.Text = "保存失败，已保存的绑定仍然有效。"; return; }
            AppLog.Info("Hotkey", $"绑定已保存：{string.Join("，", next.Select(pair => $"{pair.Key}={pair.Value}"))}");
            ApplyHotkeys();
            UpdateHotkeyTooltips();
            BindingStatus.Text = "绑定已保存。注册情况见上方；被占用的组合键可以修改后再次保存。";
            SetStatus("快捷键绑定已保存并应用。");
        }
        catch (ArgumentException ex) { BindingStatus.Text = ex.Message; SetStatus(ex.Message, true); }
    }

    private void ResetBindings_Click(object sender, RoutedEventArgs e)
    {
        PopulateBindings(HotkeyBindings.Defaults());
        BindingStatus.Text = "已填入默认绑定，点击“保存绑定”后生效。";
    }

}
