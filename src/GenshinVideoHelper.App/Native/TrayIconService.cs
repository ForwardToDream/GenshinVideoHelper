using System.IO;
using System.Windows;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace GenshinVideoHelper.App.Native;

public sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon _notify;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Drawing.Icon _icon;
    private readonly Stream _iconStream;
    public bool IsVisible => _notify.Visible;

    public TrayIconService(Window window, Action restore, Action exit)
    {
        _iconStream = Application.GetResourceStream(new Uri("pack://application:,,,/GenshinVideoHelper;component/Resources/AppIcon.ico"))!.Stream;
        _icon = new Drawing.Icon(_iconStream, 32, 32);
        _menu = new Forms.ContextMenuStrip();
        _menu.Items.Add("显示主界面", null, (_, _) => window.Dispatcher.BeginInvoke(restore));
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add("退出并关闭浏览器", null, (_, _) => window.Dispatcher.BeginInvoke(exit));
        _notify = new Forms.NotifyIcon
        {
            Icon = _icon, Text = "GenshinVideoHelper · 原神视频跟随",
            ContextMenuStrip = _menu, Visible = true
        };
        _notify.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) window.Dispatcher.BeginInvoke(restore);
        };
    }

    public void BeginExit()
    {
        _menu.Enabled = false;
        _notify.Text = "GenshinVideoHelper · 正在退出";
    }

    public void Dispose()
    {
        _notify.Visible = false;
        _notify.Dispose();
        _menu.Dispose();
        _icon.Dispose();
        _iconStream.Dispose();
    }
}