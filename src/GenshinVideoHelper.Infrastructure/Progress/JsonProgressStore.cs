using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using GenshinVideoHelper.Core.Contracts;
using GenshinVideoHelper.Core.Diagnostics;
using GenshinVideoHelper.Core.Progress;

namespace GenshinVideoHelper.Infrastructure.Progress;

/// <summary>Watch progress as one hand-editable JSON file, replaced atomically. An unreadable file is never
/// overwritten before a copy of it exists.</summary>
public sealed class JsonProgressStore(string path) : IProgressStore
{
    private const int UnreadableCopies = 3;
    private readonly string _path = Path.GetFullPath(path);
    private bool _loadedValid, _sessionCopyTaken, _protectOriginal;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        // Most parts of most videos are untouched; leaving out their defaults keeps the file short enough to edit by hand.
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { OmitEmptyLists } },
        Converters = { new JsonStringEnumConverter(), new SegmentConverter() }
    };

    private static void OmitEmptyLists(JsonTypeInfo type)
    {
        foreach (var property in type.Properties.Where(property => property.PropertyType.IsGenericType && property.PropertyType.GetGenericTypeDefinition() == typeof(IReadOnlyList<>)))
            property.ShouldSerialize = (_, value) => value is System.Collections.ICollection { Count: > 0 };
    }

    public ProgressLoadResult Load()
    {
        if (!File.Exists(_path)) return new(new ProgressDocument());
        try
        {
            var document = JsonSerializer.Deserialize<ProgressDocument>(File.ReadAllText(_path), JsonOptions) ?? throw new JsonException("进度内容为空。");
            if (document.Version > ProgressDocument.CurrentVersion)
                throw new JsonException($"进度文件版本 {document.Version} 高于本程序支持的 {ProgressDocument.CurrentVersion}。");
            _loadedValid = true;
            return new(document.Normalize());
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            AppLog.Warn("Progress", $"进度文件读取失败：{_path}", ex);
            var copy = Preserve();
            _protectOriginal = copy is null;
            return new(new ProgressDocument(), copy is null
                ? $"进度文件读取失败且无法备份，本次不会覆盖原文件：{ex.Message}"
                : $"进度文件读取失败，已从空进度开始，原文件备份为 {Path.GetFileName(copy)}：{ex.Message}");
        }
    }

    public void Save(ProgressDocument document)
    {
        if (_protectOriginal) throw new IOException("原进度文件无法读取也无法备份，为避免覆盖已停止保存。");
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var contents = JsonSerializer.Serialize(document, JsonOptions);
        // One copy of what this run started from; a file that failed to load already has its own copy.
        if (!_sessionCopyTaken && _loadedValid && File.Exists(_path)) File.Copy(_path, _path + ".bak", overwrite: true);
        _sessionCopyTaken = true;
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, contents);
        File.Move(temporary, _path, overwrite: true);
    }

    private string? Preserve()
    {
        try
        {
            var copy = _path + $".unreadable-{DateTime.Now:yyyyMMddHHmmssfff}.bak";
            File.Copy(_path, copy, overwrite: false);
            var older = Directory.GetFiles(Path.GetDirectoryName(_path)!, Path.GetFileName(_path) + ".unreadable-*.bak")
                .OrderDescending(StringComparer.Ordinal).Skip(UnreadableCopies);
            foreach (var file in older) File.Delete(file);
            AppLog.Info("Progress", $"已备份无法读取的进度文件：{copy}");
            return copy;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("Progress", "无法备份读取失败的进度文件。", ex);
            return null;
        }
    }

    // [start, end] on one line keeps a long history readable and editable by hand.
    private sealed class SegmentConverter : JsonConverter<WatchSegment>
    {
        public override WatchSegment Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("已看区间应为 [起, 止]。");
            if (!reader.Read() || reader.TokenType != JsonTokenType.Number || !reader.TryGetDouble(out var start) ||
                !reader.Read() || reader.TokenType != JsonTokenType.Number || !reader.TryGetDouble(out var end) ||
                !reader.Read() || reader.TokenType != JsonTokenType.EndArray)
                throw new JsonException("已看区间应为两个数字 [起, 止]。");
            return new(start, end);
        }

        public override void Write(Utf8JsonWriter writer, WatchSegment value, JsonSerializerOptions options) =>
            writer.WriteRawValue(string.Create(CultureInfo.InvariantCulture, $"[{value.Start:0.#}, {value.End:0.#}]"));
    }
}
