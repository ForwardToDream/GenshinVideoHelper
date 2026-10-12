namespace GenshinVideoHelper.Core.Contracts;

public sealed record ReleaseUpdate(Version Version, Uri Page);

public interface IUpdateChecker
{
    /// <summary>Returns a newer stable release, or null when none has been published.</summary>
    Task<ReleaseUpdate?> CheckAsync(Version currentVersion, CancellationToken token = default);
}
