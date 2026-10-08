using GenshinVideoHelper.Core.Application;
using GenshinVideoHelper.Core.Models;
using Xunit;

namespace GenshinVideoHelper.Core.Tests.Application;

public sealed class FollowSessionTests
{
    [Fact]
    public void Begin_NewPart_CancelsOldRequestAndIgnoresOldCompletion()
    {
        using var session = new FollowSession();
        var first = session.Begin("tab", new("BV1hjgG6jEa6", 3));
        var second = session.Begin("tab", first.Identity with { Part = 4 });
        session.Complete(first);
        Assert.True(first.Token.IsCancellationRequested);
        Assert.False(session.IsCurrent(first));
        Assert.True(session.AutomaticPending);
        session.Complete(second);
        Assert.False(session.AutomaticPending);
    }

    [Fact]
    public void Suppress_ManualClose_StaysSuppressedUntilNewIntent()
    {
        using var session = new FollowSession();
        session.Begin("tab", new("BV1hjgG6jEa6", 1));
        session.Suppress();
        Assert.False(session.AutomaticPending);
        var next = session.Begin("tab", new("BV1hjgG6jEa6", 2));
        Assert.True(session.AutomaticPending);
        Assert.Equal("new-tab", session.Attach(next, "new-tab").TargetId);
    }
}
