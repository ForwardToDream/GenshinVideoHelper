using GenshinVideoHelper.Core.Models;

namespace GenshinVideoHelper.Core.Application;

public static class BrowserSelection
{
    public static BrowserInstallation? Resolve(BrowserPreference preference, IReadOnlyList<BrowserInstallation> installed)
        => preference switch
        {
            BrowserPreference.Auto => installed.FirstOrDefault(b => b.Kind == BrowserKind.Chrome)
                ?? installed.FirstOrDefault(b => b.Kind == BrowserKind.Edge),
            BrowserPreference.Chrome => installed.FirstOrDefault(b => b.Kind == BrowserKind.Chrome),
            BrowserPreference.Edge => installed.FirstOrDefault(b => b.Kind == BrowserKind.Edge),
            _ => null
        };
}
