using System.Text.RegularExpressions;
using GenshinVideoHelper.Core.Diagnostics;
using GenshinVideoHelper.Infrastructure.Diagnostics;
using Xunit;

namespace GenshinVideoHelper.Infrastructure.Tests.Diagnostics;

public sealed class FileLogSinkTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "GenshinVideoHelper-tests", Guid.NewGuid().ToString("N"));
    private string[] Files() => Directory.GetFiles(_folder, "*.log");
    // The way a viewer reads the file while the application still has it open.
    private static string[] ReadLive(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
    }

    [Fact]
    public void Write_EntryWithException_FormatsLevelSourceAndIndentedDetails()
    {
        using (var sink = new FileLogSink(_folder))
        {
            sink.Write(LogLevel.Warn, "Chrome", "第一行\n第二行", new IOException("连接已断开"));
            Assert.True(sink.Flush(TimeSpan.FromSeconds(5)));
            var lines = ReadLive(sink.FilePath);
            Assert.Matches(new Regex(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} WARN  \[Chrome\] 第一行 第二行$"), lines[0]);
            Assert.Equal("    System.IO.IOException: 连接已断开", lines[1]);
        }
    }

    [Fact]
    public void Write_BeyondSizeLimit_RotatesAndKeepsTotalBounded()
    {
        const int limit = 2048, retained = 2;
        using (var sink = new FileLogSink(_folder, maxFileBytes: limit, retainedFiles: retained))
        {
            // Flushing in batches keeps the bounded queue from dropping entries.
            for (var index = 0; index < 600; index++)
            {
                sink.Write(LogLevel.Info, "Test", $"entry {index:0000} " + new string('x', 60), null);
                if (index % 100 == 99) Assert.True(sink.Flush(TimeSpan.FromSeconds(5)));
            }
        }
        var files = Files();
        Assert.Equal(retained + 1, files.Length);
        Assert.All(files, file => Assert.InRange(new FileInfo(file).Length, 1, limit));
        Assert.Contains("entry 0599", File.ReadAllText(Path.Combine(_folder, "app.log")));
        Assert.DoesNotContain(files, file => File.ReadAllText(file).Contains("entry 0000"));
    }

    [Fact]
    public void Write_ConsecutiveIdenticalEntries_CollapsesIntoRepeatCount()
    {
        string[] lines;
        using (var sink = new FileLogSink(_folder))
        {
            for (var index = 0; index < 50; index++) sink.Write(LogLevel.Warn, "Follow", "轮询失败", null);
            sink.Write(LogLevel.Info, "Follow", "已恢复", null);
            Assert.True(sink.Flush(TimeSpan.FromSeconds(5)));
            lines = ReadLive(sink.FilePath);
        }
        Assert.Equal(3, lines.Length);
        Assert.EndsWith("[Follow] 轮询失败", lines[0]);
        Assert.EndsWith("[Log] 上一条重复 49 次。", lines[1]);
        Assert.EndsWith("[Follow] 已恢复", lines[2]);
    }

    [Fact]
    public void Dispose_PendingEntries_AreWrittenAndLaterWritesIgnored()
    {
        var sink = new FileLogSink(_folder);
        sink.Write(LogLevel.Info, "App", "退出", null);
        sink.Dispose();
        sink.Write(LogLevel.Info, "App", "已释放后", null);
        var text = File.ReadAllText(sink.FilePath);
        Assert.Contains("退出", text);
        Assert.DoesNotContain("已释放后", text);
    }

    [Fact]
    public void Write_ExistingFile_AppendsAcrossSessions()
    {
        using (var first = new FileLogSink(_folder)) first.Write(LogLevel.Info, "App", "第一次", null);
        using (var second = new FileLogSink(_folder)) second.Write(LogLevel.Info, "App", "第二次", null);
        Assert.Equal(2, File.ReadAllLines(Path.Combine(_folder, "app.log")).Length);
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }
}
