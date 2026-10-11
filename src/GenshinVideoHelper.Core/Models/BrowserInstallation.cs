namespace GenshinVideoHelper.Core.Models;

public enum BrowserKind { Chrome, Edge }
public enum BrowserPreference { Auto, Chrome, Edge }

public sealed record BrowserInstallation(BrowserKind Kind, string ExecutablePath, string Version)
{
    public string DisplayName => Kind.ToString();
}
