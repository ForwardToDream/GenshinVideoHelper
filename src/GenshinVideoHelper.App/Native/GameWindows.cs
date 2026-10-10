using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace GenshinVideoHelper.App.Native;

public sealed record GameWindowChoice(nint Handle,string Title)
{
    public override string ToString()=>Title;
}
public static class GameWindows
{
    public static IReadOnlyList<GameWindowChoice> Find()
    {
        var list=new List<GameWindowChoice>();
        EnumWindows((hwnd,_)=>
        {
            if(!IsWindowVisible(hwnd))return true;
            GetWindowThreadProcessId(hwnd,out var pid);
            try
            {
                using var p=Process.GetProcessById((int)pid);
                if(p.ProcessName is not ("YuanShen" or "GenshinImpact" or "GenshinImpact.Cloud"))return true;
                var title=new StringBuilder(256);GetWindowText(hwnd,title,title.Capacity);
                if(title.Length>0)list.Add(new(hwnd,title.ToString()));
            }
            catch(ArgumentException){}catch(System.ComponentModel.Win32Exception){}
            return true;
        },0);
        return list;
    }
    private delegate bool EnumCallback(nint hwnd,nint param);
    [DllImport("user32.dll")]private static extern bool EnumWindows(EnumCallback callback,nint param);
    [DllImport("user32.dll")]private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(nint hwnd,out uint pid);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int GetWindowText(nint hwnd,StringBuilder title,int length);
}
