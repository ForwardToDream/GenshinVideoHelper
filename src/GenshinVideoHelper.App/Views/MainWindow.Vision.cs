using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GenshinVideoHelper.App.Native;
using GenshinVideoHelper.Core.Application;
using GenshinVideoHelper.Core.Diagnostics;
using GenshinVideoHelper.Core.Vision;
namespace GenshinVideoHelper.App;

public partial class MainWindow
{
    private MinimapOverlay? _mapOverlay;
    private readonly DispatcherTimer _visionWindowCheck=new(){Interval=TimeSpan.FromSeconds(3)};
    private VisionUpdate? _pendingVision;
    private int _visionUiQueued;
    private bool _visionReady,_visionSubscribed;
    private long _visionMetricsAt;
    private nint _gameWindow;
    private long _nextGameSearch;
    private void InitializeVision()
    {
        var options=_settings.Vision;options.Normalize();
        VisionEnabled.IsChecked=options.Enabled;VisionDirection.IsChecked=options.ShowDirection;
        SelectVisionOption(VisionInterval,options.IntervalMs);SelectVisionOption(VisionSearchInterval,options.SearchIntervalMs);SelectVisionOption(VisionMarkerSize,options.MarkerSize);
        _visionReady=true;
        _visionWindowCheck.Tick+=(_,_)=>UpdateVisionIntent();
        UpdateVisionIntent();
    }
    private static void SelectVisionOption(ComboBox combo,int value)
    {
        var item=combo.Items.Cast<ComboBoxItem>().FirstOrDefault(i=>Convert.ToInt32(i.Tag)==value);
        if(item is null){item=new ComboBoxItem{Content=value.ToString(),Tag=value.ToString()};combo.Items.Add(item);}
        combo.SelectedItem=item;
    }
    private void VisionOptions_Changed(object sender,RoutedEventArgs e)
    {
        if(!_visionReady||_closing)return;
        var settings=_settings.Vision;
        settings.Enabled=VisionEnabled.IsChecked==true;settings.ShowDirection=VisionDirection.IsChecked==true;
        if(VisionInterval.SelectedItem is ComboBoxItem interval)settings.IntervalMs=Convert.ToInt32(interval.Tag);
        if(VisionSearchInterval.SelectedItem is ComboBoxItem search)settings.SearchIntervalMs=Convert.ToInt32(search.Tag);
        if(VisionMarkerSize.SelectedItem is ComboBoxItem marker)settings.MarkerSize=Convert.ToInt32(marker.Tag);
        settings.Normalize();SaveSettings();UpdateVisionIntent();
    }
    private void UpdateVisionIntent()
    {
        if(!_visionReady||_closing)return;
        var settings=_settings.Vision;
        if(!settings.Enabled){_visionWindowCheck.Stop();Services.Vision?.Configure(null);_mapOverlay?.Hide();VisionStatus.Text="已关闭地图识别";return;}
        try
        {
            var follow=Services.Follow.Current;
            if(follow.Request is not {} request || follow.Page is not {} page || follow.Video?.PictureInPicture!=true)
            {_visionWindowCheck.Stop();Services.Vision?.Configure(null);_mapOverlay?.Hide();VisionStatus.Text="开始跟随后自动识别";return;}
            _visionWindowCheck.Start();
            if(!GameWindowCapture.IsWindow(_gameWindow) && Stopwatch.GetTimestamp()>=_nextGameSearch)
            {
                _nextGameSearch=Stopwatch.GetTimestamp()+3*Stopwatch.Frequency;
                var foreground=GameWindowCapture.GetForegroundWindow();
                var windows=GameWindows.Find();
                _gameWindow=(windows.FirstOrDefault(w=>w.Handle==foreground)??windows.FirstOrDefault())?.Handle??0;
            }
            if(_gameWindow==0 || !GameWindowCapture.IsWindow(_gameWindow))
            {Services.Vision?.Configure(null);_mapOverlay?.Hide();VisionStatus.Text="等待原神启动";return;}
            var vision=Services.EnsureVision();
            if(vision is null){VisionStatus.Text="当前视频源不支持地图采集";return;}
            if(!_visionSubscribed){vision.Updated+=OnVisionUpdate;_visionSubscribed=true;}
            vision.Configure(new(page,request.Identity,request.Version,_gameWindow,settings.IntervalMs,settings.SearchIntervalMs,settings.ShowDirection));
        }
        catch(Exception ex){AppLog.Warn("Vision","无法启用地图识别",ex);VisionStatus.Text=$"无法启用：{ex.Message}";_mapOverlay?.Hide();}
    }
    private void OnVisionUpdate(VisionUpdate update)
    {
        Interlocked.Exchange(ref _pendingVision,update);
        if(_closing||Interlocked.Exchange(ref _visionUiQueued,1)!=0)return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background,()=>
        {
            Interlocked.Exchange(ref _visionUiQueued,0);
            var latest=Interlocked.Exchange(ref _pendingVision,null);
            if(_closing||latest is null||latest.Generation!=Services.Vision?.Generation)return;
            if(!_settings.Vision.Enabled || Services.Vision?.IsConfigured != true){_mapOverlay?.Hide();return;}
            VisionStatus.Text=latest.Status;
            try
            {
                if(latest.Marker is null)_mapOverlay?.Hide();
                else (_mapOverlay??=new()).Update(latest,_settings.Vision.MarkerSize);
            }
            catch(Exception ex){_mapOverlay?.Hide();AppLog.Warn("Vision","标记窗口更新失败",ex);VisionStatus.Text="标记窗口暂不可用";}
            if(Stopwatch.GetElapsedTime(_visionMetricsAt).TotalMilliseconds>=500)
            {
                _visionMetricsAt=Stopwatch.GetTimestamp();
                VisionMetrics.Text=latest.CaptureMs>0?$"采集 {latest.CaptureMs:0.0} ms · 计算 {latest.AnalysisMs:0.0} ms · 地形相关性 {latest.Correlation:0.00} · 内点 {latest.Inliers}":"";
            }
        });
    }
    private void StopVisionUi()
    {
        _visionWindowCheck.Stop();_mapOverlay?.Hide();
        if(Services.Vision is {} vision){vision.Updated-=OnVisionUpdate;vision.Configure(null);}
    }
}
