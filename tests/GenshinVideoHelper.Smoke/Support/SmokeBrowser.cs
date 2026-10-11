using GenshinVideoHelper.Core.Models;
using GenshinVideoHelper.Infrastructure.Browser;

namespace GenshinVideoHelper.Smoke;

internal static class SmokeBrowser
{
    public static BrowserKind Kind { get; set; } = BrowserKind.Chrome;
    public static BrowserPreference Preference => Kind == BrowserKind.Chrome ? BrowserPreference.Chrome : BrowserPreference.Edge;
    public static BrowserInstallation Installation => new BrowserCatalog().Detect().FirstOrDefault(b => b.Kind == Kind)
        ?? throw new InvalidOperationException($"Install {Kind} to run this browser smoke suite.");
    public static ChromiumBrowser Create(string profile) => new(Installation, profile);
}
