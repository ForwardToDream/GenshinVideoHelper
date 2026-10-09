using System.Collections.Concurrent;
using System.Text;
using GenshinVideoHelper.Core.Diagnostics;

namespace GenshinVideoHelper.Infrastructure.Diagnostics;

/// <summary>Size-bounded log file: app.log rotates into app.1.log … app.N.log, the oldest is deleted.
/// One writer thread; callers only enqueue. Consecutive identical entries collapse into a repeat count.</summary>
public sealed class FileLogSink : ILogSink, IDisposable
{
    private const int MaxMessageLength = 8000;
    private static readonly UTF8Encoding Encoding = new(encoderShouldEmitUTF8Identifier: false);
    private readonly string _directory, _name;
    private readonly long _maxFileBytes;
    private readonly int _retainedFiles;
    private readonly BlockingCollection<Entry> _queue = new(boundedCapacity: 2048);
    private readonly Thread _writer;
    private FileStream? _stream;
    private long _size;
    private int _dropped, _disposed;
    private string? _lastKey;
    private int _repeats;

    private sealed record Entry(DateTime Time, LogLevel Level, string Source, string Message, string? Exception, ManualResetEventSlim? Flushed = null);

    public string FilePath => Path.Combine(_directory, _name + ".log");

    public FileLogSink(string directory, long maxFileBytes = 1024 * 1024, int retainedFiles = 3, string name = "app")
    {
        _directory = Path.GetFullPath(directory);
        _name = name;
        _maxFileBytes = Math.Max(1024, maxFileBytes);
        _retainedFiles = Math.Max(0, retainedFiles);
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "GenshinVideoHelper log" };
        _writer.Start();
    }

    public void Write(LogLevel level, string source, string message, Exception? exception)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (message.Length > MaxMessageLength) message = message[..MaxMessageLength] + "…";
        var entry = new Entry(DateTime.Now, level, source, message, exception?.ToString());
        try { if (!_queue.TryAdd(entry)) Interlocked.Increment(ref _dropped); }
        catch (InvalidOperationException) { } // completed concurrently by Dispose
    }

    /// <summary>Blocks until everything queued so far is on disk, or the timeout elapses.</summary>
    public bool Flush(TimeSpan timeout)
    {
        if (Volatile.Read(ref _disposed) != 0) return false;
        using var flushed = new ManualResetEventSlim();
        try
        {
            if (!_queue.TryAdd(new Entry(default, default, "", "", null, flushed), timeout)) return false;
        }
        catch (InvalidOperationException) { return false; }
        return flushed.Wait(timeout);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _queue.CompleteAdding();
        _writer.Join(TimeSpan.FromSeconds(1.5));
    }

    private void WriteLoop()
    {
        try
        {
            while (!_queue.IsCompleted)
            {
                // An idle second ends a run of repeats so the count is not withheld indefinitely.
                if (!_queue.TryTake(out var entry, TimeSpan.FromSeconds(1))) { Settle(); continue; }
                if (entry.Flushed is { } flushed)
                {
                    Settle();
                    try { flushed.Set(); } catch (ObjectDisposedException) { }
                    continue;
                }
                Append(entry);
                if (_queue.Count == 0) Commit();
            }
            Settle();
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Log writer: {ex.Message}"); }
        finally { _stream?.Dispose(); _stream = null; }
    }

    private void Append(Entry entry)
    {
        var key = $"{entry.Level}|{entry.Source}|{entry.Message}|{entry.Exception}";
        if (key == _lastKey) { _repeats++; return; }
        WriteRepeats();
        _lastKey = key;
        var dropped = Interlocked.Exchange(ref _dropped, 0);
        if (dropped > 0) WriteLine(Format(DateTime.Now, LogLevel.Warn, "Log", $"日志队列已满，丢弃 {dropped} 条。", null));
        WriteLine(Format(entry.Time, entry.Level, entry.Source, entry.Message, entry.Exception));
    }

    private void Settle() { WriteRepeats(); _lastKey = null; Commit(); }

    private void WriteRepeats()
    {
        if (_repeats == 0) return;
        WriteLine(Format(DateTime.Now, LogLevel.Info, "Log", $"上一条重复 {_repeats} 次。", null));
        _repeats = 0;
    }

    private static string Format(DateTime time, LogLevel level, string source, string message, string? exception)
    {
        var text = new StringBuilder(160)
            .Append(time.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(' ')
            .Append(level.ToString().ToUpperInvariant().PadRight(5)).Append(" [").Append(source).Append("] ")
            .Append(message.ReplaceLineEndings(" ")).Append("\r\n");
        if (exception is not null)
            foreach (var line in exception.ReplaceLineEndings("\n").Split('\n')) text.Append("    ").Append(line).Append("\r\n");
        return text.ToString();
    }

    private void WriteLine(string text)
    {
        try
        {
            var bytes = Encoding.GetBytes(text);
            if (_stream is null) Open();
            if (_size > 0 && _size + bytes.Length > _maxFileBytes) Rotate();
            _stream!.Write(bytes);
            _size += bytes.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Unwritable location: drop the entry and retry opening on the next one.
            _stream?.Dispose();
            _stream = null;
        }
    }

    private void Commit()
    {
        try { _stream?.Flush(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _stream?.Dispose(); _stream = null; }
    }

    private void Open()
    {
        Directory.CreateDirectory(_directory);
        _stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.Read | FileShare.Delete);
        _size = _stream.Length;
    }

    private void Rotate()
    {
        _stream!.Dispose();
        _stream = null;
        try
        {
            if (_retainedFiles == 0) File.Delete(FilePath);
            else
            {
                File.Delete(Archive(_retainedFiles));
                for (var index = _retainedFiles - 1; index >= 1; index--)
                    if (File.Exists(Archive(index))) File.Move(Archive(index), Archive(index + 1), overwrite: true);
                File.Move(FilePath, Archive(1), overwrite: true);
            }
            Open();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An archive is held open elsewhere: the size limit still applies, so start the current file over.
            _stream?.Dispose();
            _stream = new FileStream(FilePath, FileMode.Create, FileAccess.Write, FileShare.Read | FileShare.Delete);
            _size = 0;
        }
    }

    private string Archive(int index) => Path.Combine(_directory, $"{_name}.{index}.log");
}
