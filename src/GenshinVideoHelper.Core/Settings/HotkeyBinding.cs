namespace GenshinVideoHelper.Core.Settings;

public enum HotkeyAction { TogglePlayback, SeekBackward, SeekForward, TogglePip, PlacePip, ToggleMute, PreviousEpisode, NextEpisode, ReversePipVisibility }

public readonly record struct HotkeyGesture(uint Modifiers, uint Key, string Text)
{
    private static readonly Dictionary<string, uint> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Left"] = 0x25, ["Up"] = 0x26, ["Right"] = 0x27, ["Down"] = 0x28,
        ["Home"] = 0x24, ["End"] = 0x23, ["PageUp"] = 0x21, ["PageDown"] = 0x22,
        ["Space"] = 0x20, ["Enter"] = 0x0D, ["Tab"] = 0x09, ["Escape"] = 0x1B,
        ["Backspace"] = 0x08, ["Delete"] = 0x2E, ["Insert"] = 0x2D,
        ["~"] = 0xC0, ["`"] = 0xC0, ["Oem3"] = 0xC0
    };

    public static bool TryParse(string? text, out HotkeyGesture gesture, bool allowUnmodified = false)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var tokens = text.Split('+', StringSplitOptions.TrimEntries);
        uint modifiers = 0;
        foreach (var token in tokens[..^1])
        {
            uint modifier = token.ToUpperInvariant() switch { "ALT" => 1, "CTRL" or "CONTROL" => 2, "SHIFT" => 4, "WIN" or "WINDOWS" => 8, _ => 0 };
            if (modifier == 0 || (modifiers & modifier) != 0) return false;
            modifiers |= modifier;
        }
        if (modifiers == 0 && !allowUnmodified) return false;
        var name = tokens[^1].ToUpperInvariant();
        uint key;
        if (name.Length == 1 && (name[0] is >= 'A' and <= 'Z' or >= '0' and <= '9')) key = name[0];
        else if (name.StartsWith('F') && int.TryParse(name[1..], out var number) && number is >= 1 and <= 24) { key = (uint)(0x70 + number - 1); name = $"F{number}"; }
        else if (NamedKeys.TryGetValue(name, out key)) name = NamedKeys.First(pair => pair.Value == key).Key;
        else return false;
        var prefix = new List<string>();
        if ((modifiers & 2) != 0) prefix.Add("Ctrl");
        if ((modifiers & 1) != 0) prefix.Add("Alt");
        if ((modifiers & 4) != 0) prefix.Add("Shift");
        if ((modifiers & 8) != 0) prefix.Add("Win");
        gesture = new(modifiers, key, string.Join('+', prefix.Append(name)));
        return true;
    }
}

public static class HotkeyBindings
{
    public static Dictionary<HotkeyAction, string> Defaults() => new()
    {
        [HotkeyAction.SeekBackward] = "Alt+1", [HotkeyAction.TogglePlayback] = "Alt+2", [HotkeyAction.SeekForward] = "Alt+3",
        [HotkeyAction.PreviousEpisode] = "Alt+Left", [HotkeyAction.NextEpisode] = "Alt+Right",
        [HotkeyAction.TogglePip] = "Alt+P", [HotkeyAction.PlacePip] = "Alt+Home", [HotkeyAction.ToggleMute] = "Alt+End",
        [HotkeyAction.ReversePipVisibility] = "~"
    };

    public static string Title(HotkeyAction action) => action switch
    {
        HotkeyAction.SeekBackward => "后退", HotkeyAction.TogglePlayback => "暂停 / 继续", HotkeyAction.SeekForward => "前进",
        HotkeyAction.PreviousEpisode => "上一分集", HotkeyAction.NextEpisode => "下一分集",
        HotkeyAction.TogglePip => "画中画开关", HotkeyAction.PlacePip => "放回左下角", HotkeyAction.ToggleMute => "静音 / 恢复声音", HotkeyAction.ReversePipVisibility => "反转浮窗隐藏（按住）", _ => action.ToString()
    };

    // Missing entries get new defaults; existing invalid or duplicate bindings are reported before registration.
    public static Dictionary<HotkeyAction, string> Validate(IReadOnlyDictionary<HotkeyAction, string> bindings)
    {
        var result = new Dictionary<HotkeyAction, string>();
        var used = new HashSet<(uint, uint)>();
        foreach (var (action, fallback) in Defaults())
        {
            var value = bindings.GetValueOrDefault(action, fallback);
            if (!HotkeyGesture.TryParse(value, out var gesture, allowUnmodified: action == HotkeyAction.ReversePipVisibility)) throw new ArgumentException(action == HotkeyAction.ReversePipVisibility
                ? $"{Title(action)}：请输入单键或组合键，例如 ~、F8 或 Ctrl+Shift+F8。"
                : $"{Title(action)}：请输入修饰键加按键，例如 Alt+Left 或 Ctrl+Shift+F8。");
            if (!used.Add((gesture.Modifiers, gesture.Key))) throw new ArgumentException($"{Title(action)}：{gesture.Text} 与另一项绑定重复。");
            result[action] = gesture.Text;
        }
        return result;
    }
}
