using GenshinVideoHelper.Core.Models;
using GenshinVideoHelper.Infrastructure.Browser;
using Xunit;

namespace GenshinVideoHelper.Infrastructure.Tests.Browser;

public sealed class BrowserCatalogTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "GenshinVideoHelper-tests", Guid.NewGuid().ToString("N"));
    private string FileFor(string name)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, "fixture");
        return path;
    }

    [Fact]
    public void StaleRegistryPath_IsSkippedForExistingSystemInstallation()
    {
        var chrome = FileFor("chrome.exe");
        var catalog = new BrowserCatalog(kind => kind == BrowserKind.Chrome ? [Path.Combine(_directory, "missing", "chrome.exe"), $"\"{chrome}\""] : [], _ => "155.0.1.2");
        var installation = Assert.Single(catalog.Detect());
        Assert.Equal(Path.GetFullPath(chrome), installation.ExecutablePath);
        Assert.Equal("155.0.1.2", installation.Version);
    }

    [Theory]
    [InlineData("MicrosoftEdge.exe", "44.0.0.0")]
    [InlineData("msedge.exe", "78.0.0.0")]
    [InlineData("msedge.exe", "unknown")]
    public void LegacyOrInvalidEdge_IsNotOffered(string name, string version)
    {
        var path = FileFor(name);
        Assert.Empty(new BrowserCatalog(kind => kind == BrowserKind.Edge ? [path] : [], _ => version).Detect());
    }

    [Fact]
    public void Detect_ReturnsBothAndRechecksRemovedFiles()
    {
        var chrome = FileFor("chrome.exe"); var edge = FileFor("msedge.exe");
        var catalog = new BrowserCatalog(kind => [kind == BrowserKind.Chrome ? chrome : edge], _ => "155.0.0.0");
        Assert.Equal(2, catalog.Detect().Count);
        File.Delete(chrome); File.Delete(edge);
        Assert.Empty(catalog.Detect());
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
