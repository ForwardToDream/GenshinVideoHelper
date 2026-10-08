using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using GenshinVideoHelper.Core.Settings;

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
            // Hold/release is observed by the PiP service; WM_HOTKEY only reports presses.
            if (action == HotkeyAction.ReversePipVisibility) continue;
            HotkeyGesture.TryParse(text, out var gesture);
            var id = 0x5600 + (int)action;
            if (!RegisterHotKey(_source.Handle, id, gesture.Modifiers | 0x4000, gesture.Key))
            {
                var error = Marshal.GetLastWin32Error();
                conflicts.Add($"{HotkeyBindings.Title(action)} {text}（{new Win32Exception(error).Message}）");
                continue;
            }
            _registered.Add(id);
        }
        return conflicts;
    }

    public void Disable()
    {
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

