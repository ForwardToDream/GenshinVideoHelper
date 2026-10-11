using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;
using GenshinVideoHelper.Core.Contracts;
using GenshinVideoHelper.Core.Models;

namespace GenshinVideoHelper.Infrastructure.Browser;

/// <summary>Only existing desktop Chromium executables are offered; detection never starts a browser.</summary>
public sealed class BrowserCatalog : IBrowserCatalog
{
    private readonly Func<BrowserKind, IEnumerable<string>> _candidates;
    private readonly Func<string, string?> _version;

    public BrowserCatalog() : this(InstalledPaths, ReadVersion) { }

    internal BrowserCatalog(Func<BrowserKind, IEnumerable<string>> candidates, Func<string, string?> version)
    { _candidates = candidates; _version = version; }

    public IReadOnlyList<BrowserInstallation> Detect()
    {
        var installed = new List<BrowserInstallation>();
        foreach (var kind in Enum.GetValues<BrowserKind>())
        {
            var executable = kind == BrowserKind.Chrome ? "chrome.exe" : "msedge.exe";
            foreach (var candidate in _candidates(kind).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(candidate.Trim().Trim('"')));
                    if (!string.Equals(Path.GetFileName(path), executable, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) continue;
                    var version = _version(path);
                    // EdgeHTML uses MicrosoftEdge.exe. Also reject pre-Chromium/invalid Edge versions.
                    if (kind == BrowserKind.Edge && (!Version.TryParse(version, out var parsed) || parsed.Major < 79)) continue;
                    installed.Add(new(kind, path, string.IsNullOrWhiteSpace(version) ? "未知版本" : version));
                    break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
            }
        }
        return installed;
    }

    private static string? ReadVersion(string path) => FileVersionInfo.GetVersionInfo(path).ProductVersion?.Split(' ')[0];

    private static IEnumerable<string> InstalledPaths(BrowserKind kind)
    {
        var executable = kind == BrowserKind.Chrome ? "chrome.exe" : "msedge.exe";
        if (OperatingSystem.IsWindows())
            foreach (var path in AppPaths(executable)) yield return path;
        var relative = kind == BrowserKind.Chrome ? "Google/Chrome/Application/chrome.exe" : "Microsoft/Edge/Application/msedge.exe";
        foreach (var folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
                     Environment.SpecialFolder.LocalApplicationData })
        {
            var root = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(root)) yield return Path.Combine(root, relative);
        }
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<string> AppPaths(string executable)
    {
        var paths = new List<string>();
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var key = root.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{executable}");
                if (key?.GetValue(null) is string path && !string.IsNullOrWhiteSpace(path)) paths.Add(path);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        }
        return paths;
    }
}
