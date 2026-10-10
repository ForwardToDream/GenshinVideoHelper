using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using GenshinVideoHelper.Core.Settings;
using GenshinVideoHelper.Core.Diagnostics;

namespace GenshinVideoHelper.App.Native;

public sealed class HotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private readonly HwndSource _source;
    private readonly List<int> _registered = [];
    public event Action<HotkeyAction>? Pressed;


    public HotkeyService(nint window)
    {
        _source = HwndSource.FromHwnd(window)!;
        _source.AddHook(WindowProcedure);
    }

    public int RegisteredCount => _registered.Count;
    public bool IsRegistered(HotkeyAction action) => _registered.Contains(0x5600 + (int)action);

    public IReadOnlyList<string> Enable(IReadOnlyDictionary<HotkeyAction, string>? bindings = null)
    {
        var validated = HotkeyBindings.Validate(bindings ?? HotkeyBindings.Defaults());
        Disable();
        var conflicts = new List<string>();
        foreach (var (action, text) in validated)
        {
            HotkeyGesture.TryParse(text, out var gesture, allowUnmodified: action == HotkeyAction.ReversePipVisibility);
            var id = 0x5600 + (int)action;
            if (!RegisterHotKey(_source.Handle, id, gesture.Modifiers | 0x4000, gesture.Key))
            {
                var error = Marshal.GetLastWin32Error();
                conflicts.Add($"{HotkeyBindings.Title(action)} {text}（{new Win32Exception(error).Message}）");
                AppLog.Warn("Hotkey", $"{action} {text} 注册失败，错误码 {error}。");
                continue;
            }
            _registered.Add(id);
        }
        AppLog.Info("Hotkey", $"已注册 {_registered.Count} 项全局快捷键，{conflicts.Count} 项被占用。");
        return conflicts;
    }

    public void Disable()
    {
        if (_registered.Count > 0) AppLog.Info("Hotkey", $"注销 {_registered.Count} 项全局快捷键。");
        foreach (var id in _registered) UnregisterHotKey(_source.Handle, id);
        _registered.Clear();
    }

    private nint WindowProcedure(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmHotkey && _registered.Contains((int)wParam))
        {
            handled = true;
            Pressed?.Invoke((HotkeyAction)((int)wParam - 0x5600));
        }
        return 0;
    }

    public void Dispose()
    {
        Disable();
        _source.RemoveHook(WindowProcedure);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint hwnd, int id);
}
