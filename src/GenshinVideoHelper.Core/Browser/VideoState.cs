namespace GenshinVideoHelper.Core.Browser;

public sealed record VideoState(
    string Title,
    string Url,
    bool Paused,
    double CurrentTime,
    double? Duration,
    bool Muted,
    double PlaybackRate,
    bool PictureInPicture,
    int VideoWidth,
    int VideoHeight,
    long? Cid = null);

public sealed record VideoCommand(string Action, double Value = 0, bool Absolute = false);

public sealed record BrowserPage(string Id, string Title, string Url, string WebSocketDebuggerUrl)
{
    public override string ToString() => string.IsNullOrWhiteSpace(Title) ? Url : Title;
}
