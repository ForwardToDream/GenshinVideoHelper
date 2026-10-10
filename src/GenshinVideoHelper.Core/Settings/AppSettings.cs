namespace GenshinVideoHelper.Core.Settings;

public sealed class AppSettings
{
    public Vision.VisionSettings Vision { get; set; } = new();
    public string VideoUrl { get; set; } = "";
    public string? SelectedVideoLibraryId { get; set; } = Library.VideoLibraryCatalog.DefaultLibraryId;
    public int SeekSeconds { get; set; } = 5;
    public int PipWidth { get; set; } = 420;
    public int PipMargin { get; set; } = 20;
    public bool HotkeysEnabled { get; set; } = true;
    public Dictionary<HotkeyAction, string> Hotkeys { get; set; } = HotkeyBindings.Defaults();
    public Diagnostics.LogLevel LogLevel { get; set; } = Diagnostics.LogLevel.Info;
    /// <summary>Share of an episode that must actually have played before it completes on its own.</summary>
    public int ProgressCompletionPercent { get; set; } = 90;

    public void Normalize()
    {
        (Vision ??= new()).Normalize();
        ProgressCompletionPercent = Math.Clamp(ProgressCompletionPercent, 50, 100);
        if (!Enum.IsDefined(LogLevel)) LogLevel = Diagnostics.LogLevel.Info;
        SeekSeconds = Math.Clamp(SeekSeconds, 1, 60);
        PipWidth = Math.Clamp(PipWidth, 280, 800);
        PipMargin = Math.Clamp(PipMargin, 0, 100);
        if (!Models.VideoIdentity.TryParse(VideoUrl, out _)) VideoUrl = "";
        Hotkeys = HotkeyBindings.Validate(Hotkeys ?? HotkeyBindings.Defaults());
    }
}
