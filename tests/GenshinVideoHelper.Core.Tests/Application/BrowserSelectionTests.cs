using GenshinVideoHelper.Core.Application;
using GenshinVideoHelper.Core.Models;
using Xunit;

namespace GenshinVideoHelper.Core.Tests.Application;

public sealed class BrowserSelectionTests
{
    [Theory]
    [InlineData(true, false, BrowserKind.Chrome)]
    [InlineData(false, true, BrowserKind.Edge)]
    [InlineData(true, true, BrowserKind.Chrome)]
    [InlineData(false, false, null)]
    public void Auto_PrefersChromeRegardlessOfDiscoveryOrder(bool chrome, bool edge, BrowserKind? expected)
    {
        var installed = new List<BrowserInstallation>();
        if (edge) installed.Add(new(BrowserKind.Edge, "edge", "155"));
        if (chrome) installed.Add(new(BrowserKind.Chrome, "chrome", "155"));
        Assert.Equal(expected, BrowserSelection.Resolve(BrowserPreference.Auto, installed)?.Kind);
    }

    [Theory]
    [InlineData(BrowserPreference.Chrome, BrowserKind.Chrome)]
    [InlineData(BrowserPreference.Edge, BrowserKind.Edge)]
    public void ManualChoice_NeverFallsBack(BrowserPreference preference, BrowserKind kind)
    {
        BrowserInstallation[] both = [new(BrowserKind.Edge, "edge", "155"), new(BrowserKind.Chrome, "chrome", "155")];
        Assert.Equal(kind, BrowserSelection.Resolve(preference, both)!.Kind);
        Assert.Null(BrowserSelection.Resolve(preference, both.Where(b => b.Kind != kind).ToArray()));
    }
}
