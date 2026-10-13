using System.Reflection;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Runtime.Loader;

// No references to the application's DLLs: every dependency must come from the compressed assembly.
var root = Path.GetFullPath(args[0]);
var appPath = Path.GetFullPath(args[1]);
Check(Environment.Version.Major == 8, "Package checks use .NET 8");
var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(appPath);
var resources = assembly.GetManifestResourceNames();
Check(resources.Contains("costura.microsoft.windows.sdk.net.dll.compressed") &&
    resources.Contains("costura_win_x64.opencvsharpextern.dll.compressed"), "SDK and native OpenCV are compressed");
RuntimeHelpers.RunModuleConstructor(assembly.ManifestModule.ModuleHandle);
var cv = Assembly.Load("OpenCvSharp");
var infrastructure = Assembly.Load("GenshinVideoHelper.Infrastructure");
var windows = Assembly.Load("Microsoft.Windows.SDK.NET");
Check(cv.Location == "" && infrastructure.Location == "" && windows.Location == "", "Managed dependencies load from resources without loose DLLs");
var cv2 = cv.GetType("OpenCvSharp.Cv2", true)!;
var info = (string)cv2.GetMethod("GetBuildInformation")!.Invoke(null, null)!;
Check(info.Contains("4.13.0"), "Expected slim native OpenCV loaded");
Check(!NativeModule.FilePath("OpenCvSharpExtern.dll").StartsWith(Path.GetDirectoryName(appPath)!, StringComparison.OrdinalIgnoreCase),
    "Native DLL loads from the compressed dependency cache rather than developer output");
var analyzer = (IDisposable)Activator.CreateInstance(infrastructure.GetType("GenshinVideoHelper.Infrastructure.Vision.MinimapAnalyzer", true)!)!;
analyzer.Dispose();

var mat = cv.GetType("OpenCvSharp.Mat", true)!;
var inputType = cv.GetType("OpenCvSharp.InputArray", true)!;
var outputType = cv.GetType("OpenCvSharp.OutputArray", true)!;
var mode = cv.GetType("OpenCvSharp.ImreadModes", true)!;
var orbType = cv.GetType("OpenCvSharp.ORB", true)!;
var factory = orbType.GetMethod("Create", BindingFlags.Public | BindingFlags.Static)!;
using var orb = (IDisposable)factory.Invoke(null, factory.GetParameters().Select(p => p.DefaultValue).ToArray())!;
var imread = cv2.GetMethod("ImRead", [typeof(string), mode])!;
var detect = orbType.GetMethods().Single(m => m.Name == "DetectAndCompute" && m.GetParameters().Length == 5);
var inputCreate = inputType.GetMethod("Create", [mat])!;
var outputCreate = outputType.GetMethod("Create", [mat])!;
foreach (var name in new[] { "user-start.png", "guide-start.png" })
{
    using var image = (IDisposable)imread.Invoke(null, [Path.Combine(root, "tests", "Fixtures", "Minimap", "basic", "images", name), Enum.ToObject(mode, 1)])!;
    using var descriptors = (IDisposable)Activator.CreateInstance(mat)!;
    using var input = (IDisposable)inputCreate.Invoke(null, [image])!;
    using var output = (IDisposable)outputCreate.Invoke(null, [descriptors])!;
    object?[] parameters = [input, null, null, output, false];
    detect.Invoke(orb, parameters);
    var points = (Array)parameters[2]!;
    Check(points.Length >= 20 && (int)mat.GetProperty("Rows")!.GetValue(descriptors)! == points.Length,
        $"ORB reads real fixture {name} and produces descriptors");
}
Console.WriteLine("PASS: compressed managed dependencies, native OpenCV, analyzer and real-image ORB under .NET 8.");
if (args.Length > 2)
{
    var parent = Path.GetFullPath(Path.Combine(root, "artifacts", "test-run"));
    var temp = Path.GetFullPath(Path.Combine(parent, "package-runtime-" + Guid.NewGuid().ToString("N")));
    if (!temp.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Test path escaped its root.");
    Directory.CreateDirectory(temp);
    try
    {
        var dotnet = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory()).Parent!.Parent!.Parent!.FullName;
        var fxr = Directory.GetDirectories(Path.Combine(dotnet, "host", "fxr")).OrderByDescending(p => Version.Parse(Path.GetFileName(p))).First();
        foreach (var scenario in new[] { "host", "desktop" })
        {
            var missing = Directory.CreateDirectory(Path.Combine(temp, scenario)).FullName;
            if (scenario == "desktop")
            {
                var fxrTarget = Directory.CreateDirectory(Path.Combine(missing, "host", "fxr", Path.GetFileName(fxr))).FullName;
                File.Copy(Path.Combine(fxr, "hostfxr.dll"), Path.Combine(fxrTarget, "hostfxr.dll"));
            }
            var start = new ProcessStartInfo(Path.GetFullPath(args[2])) { UseShellExecute = false, RedirectStandardError = true, WindowStyle = ProcessWindowStyle.Hidden };
            start.Environment["DOTNET_ROOT_X64"] = missing;
            start.Environment["DOTNET_MULTILEVEL_LOOKUP"] = "0";
            start.Environment["DOTNET_DISABLE_GUI_ERRORS"] = "1";
            using var process = Process.Start(start)!;
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(5000)) { process.Kill(entireProcessTree: true); process.WaitForExit(); throw new InvalidOperationException("Missing-runtime launch did not exit."); }
            var text = error.GetAwaiter().GetResult();
            Check(process.ExitCode != 0 && text.Contains(".NET") && text.Contains("https://aka.ms/dotnet-core-applaunch"), $"Native apphost supplies an installation link without {scenario}");
            if (scenario == "desktop") Check(text.Contains("framework=Microsoft.WindowsDesktop.App") && text.Contains("framework_version=8.0.0") && text.Contains("arch=x64"), "Installation link selects .NET 8 Desktop Runtime x64");
        }
    }
    finally { Directory.Delete(temp, recursive: true); }
}

static void Check(bool success, string message)
{
    if (!success) throw new InvalidOperationException(message);
    Console.WriteLine(message);
}

internal static class NativeModule
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetModuleFileName(nint module, StringBuilder filename, int size);
    internal static string FilePath(string name)
    {
        var module = GetModuleHandle(name);
        var path = new StringBuilder(32768);
        if (module == 0 || GetModuleFileName(module, path, path.Capacity) == 0) throw new InvalidOperationException("Native OpenCV module was not found.");
        return path.ToString();
    }
}
