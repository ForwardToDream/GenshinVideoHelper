using GenshinVideoHelper.Core.Models;

namespace GenshinVideoHelper.Core.Application;

public sealed record FollowRequest(long Version, string TargetId, VideoIdentity Identity, CancellationToken Token);

/// <summary>One intent per video/part; completed or manually closed PiP is never reopened by polling.</summary>
public sealed class FollowSession : IDisposable
{
    private readonly CancellationToken _lifetime;
    private CancellationTokenSource? _requestCancellation;
    private long _version;
    public FollowRequest? Current { get; private set; }
    public bool AutomaticPending { get; private set; }

    public FollowSession(CancellationToken lifetime = default) => _lifetime = lifetime;

    public FollowRequest Begin(string targetId, VideoIdentity identity)
    {
        _requestCancellation?.Cancel();
        _requestCancellation?.Dispose();
        _requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        Current = new(++_version, targetId, identity, _requestCancellation.Token);
        AutomaticPending = true;
        return Current;
    }

    public FollowRequest Attach(FollowRequest request, string targetId)
    {
        if (!IsCurrent(request)) throw new OperationCanceledException(request.Token);
        Current = request with { TargetId = targetId };
        return Current;
    }

    public bool IsCurrent(FollowRequest request) => Current?.Version == request.Version && !request.Token.IsCancellationRequested;
    public void Complete(FollowRequest request) { if (IsCurrent(request)) AutomaticPending = false; }
    public void Suppress() => AutomaticPending = false;
    public void Dispose() { _requestCancellation?.Cancel(); _requestCancellation?.Dispose(); }
}
