using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Sim.GodotShell;

public sealed record ReviewEvent(double Time, string Type, IReadOnlyDictionary<string, string?> Values);
public sealed record ReviewRecording(string Format, IReadOnlyList<ReviewEvent> Events);
public sealed record ReviewPose(int EventIndex, double Time, string Role, double X, double Y, double Heading, string Source);
public sealed record ReviewSample(double Time, IReadOnlyDictionary<string, double?> Values);
public sealed record ReviewSeries(string Source, IReadOnlyList<string> Channels, IReadOnlyList<ReviewSample> Rows);
public sealed record ReviewStats(int Count, double Min, double Max, double Mean);
public sealed record ReviewReference(string Name, string Source, int StartIndex, int EndIndex, double X, double Y, double Heading, IReadOnlyDictionary<string, ReviewStats> Stats);

public static class RealMatchReviewData
{
    private const string PatrolHeader = "t,front,rear,left,right,alert,on_stage,action,ir1,ir2";
    private const string GrayHeader = "t,front,rear,left,right";
    private const string ShovelHeader = "t,left,right,valid";
    private const string ReferenceFormat = "real-match-references-v1";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private sealed record ReferenceFile(string Format, IReadOnlyList<ReviewReference> References);

    public static IReadOnlyList<ReviewPose> ExtractPoses(ReviewRecording recording)
    {
        ArgumentNullException.ThrowIfNull(recording);
        var poses = new List<ReviewPose>();
        if (recording.Format != "real-match-v1") return poses;
        for (int i = 0; i < recording.Events.Count; i++)
        {
            var item = recording.Events[i];
            if (item.Type != "pose") continue;
            string Required(string name)
            {
                if (!item.Values.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
                    throw new FormatException($"位姿事件 {i} 缺少或无效字段：{name}。");
                return value;
            }
            var role = Required("role");
            if (role != "us" && role != "them") throw new FormatException($"位姿事件 {i} 的 role 必须为 us 或 them。");
            poses.Add(new ReviewPose(i, item.Time, role, Number(Required("x"), $"位姿事件 {i} 的 x"),
                Number(Required("y"), $"位姿事件 {i} 的 y"), Number(Required("heading"), $"位姿事件 {i} 的 heading"), Required("source")));
        }
        return poses;
    }

    public static ReviewPose? PoseAtOrBefore(IReadOnlyList<ReviewPose> poses, string role, int eventIndex)
    {
        ReviewPose? latest = null;
        foreach (var pose in poses)
            if (pose.Role == role && pose.EventIndex <= eventIndex && (latest == null || pose.EventIndex > latest.EventIndex)) latest = pose;
        return latest;
    }

    public static ReviewRecording LoadRecording(string path)
    {
        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text)) throw new FormatException("记录文件为空。");
        if (text.TrimStart().StartsWith("{", StringComparison.Ordinal)) return ReadJsonRecording(text);
        var rows = ReadCsv(text, out var header);
        if (header != PatrolHeader) throw new FormatException("不支持的巡台 CSV 表头。");
        var names = header.Split(',');
        var events = new List<ReviewEvent>();
        double previous = double.NegativeInfinity;
        foreach (var row in rows)
        {
            if (row.Count != names.Length) throw new FormatException("巡台 CSV 列数不匹配。");
            var time = Number(row[0], "时间"); CheckTime(time, ref previous);
            var values = new Dictionary<string, string?>();
            for (int i = 1; i < names.Length; i++)
            {
                if (i != 7 && row[i].Length != 0) Number(row[i], names[i]);
                values.Add(names[i], row[i].Length == 0 ? null : row[i]);
            }
            events.Add(new ReviewEvent(time, "patrol", values));
        }
        return new ReviewRecording("patrol-csv", events);
    }

    public static ReviewSeries LoadSeries(string path)
    {
        var rows = ReadCsv(File.ReadAllText(path), out var header);
        if (header != GrayHeader && header != ShovelHeader) throw new FormatException("不支持的采样 CSV 表头。");
        var names = header.Split(',').Skip(1).ToArray();
        var samples = new List<ReviewSample>();
        double previous = double.NegativeInfinity;
        foreach (var row in rows)
        {
            if (row.Count != names.Length + 1) throw new FormatException("采样 CSV 列数不匹配。");
            var time = Number(row[0], "时间"); CheckTime(time, ref previous);
            var values = new Dictionary<string, double?>();
            for (int i = 0; i < names.Length; i++) values.Add(names[i], row[i + 1].Length == 0 ? null : Number(row[i + 1], names[i]));
            samples.Add(new ReviewSample(time, values));
        }
        return new ReviewSeries(path, names, samples);
    }

    public static ReviewReference MakeReference(ReviewSeries series, string name, int startIndex, int endIndex, double x, double y, double heading)
    {
        ArgumentNullException.ThrowIfNull(series);
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(series.Source)) throw new ArgumentException("参考点名称和来源不能为空。");
        if (startIndex < 0 || endIndex < startIndex || endIndex >= series.Rows.Count) throw new ArgumentOutOfRangeException(nameof(startIndex), "静止区间索引无效。");
        Finite(x, "X"); Finite(y, "Y"); Finite(heading, "朝向");
        var stats = new Dictionary<string, ReviewStats>();
        foreach (var channel in series.Channels)
        {
            if (string.IsNullOrWhiteSpace(channel) || stats.ContainsKey(channel)) throw new ArgumentException("采样通道名称无效或重复。");
            int count = 0; double min = 0, max = 0, mean = 0;
            for (int i = startIndex; i <= endIndex; i++)
            {
                if (!series.Rows[i].Values.TryGetValue(channel, out var value)) throw new ArgumentException("采样行缺少通道：" + channel);
                if (!value.HasValue) continue;
                double v = value.Value; Finite(v, channel);
                if (count++ == 0) min = max = mean = v;
                else { min = Math.Min(min, v); max = Math.Max(max, v); mean = mean * ((double)(count - 1) / count) + v / count; }
            }
            Finite(mean, "均值");
            if (count > 0) mean = Math.Clamp(mean, min, max);
            stats.Add(channel, new ReviewStats(count, min, max, mean));
        }
        return new ReviewReference(name, series.Source, startIndex, endIndex, x, y, heading, stats);
    }

    public static IReadOnlyList<ReviewReference> LoadReferences(string path)
    {
        var text = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("references", out var refs) || refs.ValueKind != JsonValueKind.Array) throw new FormatException("参考点列表无效。");
        foreach (var item in refs.EnumerateArray())
        {
            RequireProperties(item, "name", "source", "startIndex", "endIndex", "x", "y", "heading", "stats");
            var stats = item.GetProperty("stats");
            if (stats.ValueKind != JsonValueKind.Object) throw new FormatException("参考点统计无效。");
            foreach (var stat in stats.EnumerateObject()) RequireProperties(stat.Value, "count", "min", "max", "mean");
        }
        var file = JsonSerializer.Deserialize<ReferenceFile>(text, JsonOptions) ?? throw new FormatException("参考点文件为空。");
        if (file.Format != ReferenceFormat || file.References == null) throw new FormatException("参考点文件版本或内容无效。");
        ValidateReferences(file.References);
        return file.References;
    }

    private static void RequireProperties(JsonElement item, params string[] names)
    {
        if (item.ValueKind != JsonValueKind.Object) throw new FormatException("参考点结构无效。");
        foreach (var name in names)
            if (!item.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) throw new FormatException("缺少参考点字段：" + name);
    }

    public static void SaveReferences(string path, IReadOnlyList<ReviewReference> references)
    {
        ArgumentNullException.ThrowIfNull(references);
        ValidateReferences(references);
        var fullPath = Path.GetFullPath(path);
        var tempPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(new ReferenceFile(ReferenceFormat, references), JsonOptions));
            File.Move(tempPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    private static void ValidateReferences(IReadOnlyList<ReviewReference> references)
    {
        foreach (var r in references)
        {
            if (r == null || string.IsNullOrWhiteSpace(r.Name) || string.IsNullOrWhiteSpace(r.Source) || r.StartIndex < 0 || r.EndIndex < r.StartIndex || r.Stats == null) throw new FormatException("参考点字段无效。");
            Finite(r.X, "X"); Finite(r.Y, "Y"); Finite(r.Heading, "朝向");
            foreach (var (name, s) in r.Stats)
            {
                if (string.IsNullOrWhiteSpace(name) || s == null || s.Count < 0 || s.Count > (long)r.EndIndex - r.StartIndex + 1) throw new FormatException("参考点统计无效。");
                Finite(s.Min, "最小值"); Finite(s.Max, "最大值"); Finite(s.Mean, "均值");
                if (s.Count == 0 ? s.Min != 0 || s.Max != 0 || s.Mean != 0 : s.Min > s.Max || s.Mean < s.Min || s.Mean > s.Max) throw new FormatException("参考点统计范围无效。");
            }
        }
    }

    private static ReviewRecording ReadJsonRecording(string text)
    {
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.String || format.GetString() != "real-match-v1" || !root.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array) throw new FormatException("记录 JSON 的 format 或 events 无效。");
        var result = new List<ReviewEvent>(); double previous = double.NegativeInfinity;
        foreach (var item in events.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("time", out var t) || t.ValueKind != JsonValueKind.Number || !t.TryGetDouble(out var time) || !item.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(type.GetString()) || !item.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Object) throw new FormatException("记录事件字段无效。");
            CheckTime(time, ref previous);
            var fields = new Dictionary<string, string?>();
            foreach (var p in values.EnumerateObject())
            {
                if (string.IsNullOrWhiteSpace(p.Name) || fields.ContainsKey(p.Name)) throw new FormatException("事件字段名为空或重复。");
                var v = p.Value;
                if (type.GetString() == "pose" && ((p.Name is "role" or "source" && v.ValueKind != JsonValueKind.String) ||
                    (p.Name is "x" or "y" or "heading" && v.ValueKind != JsonValueKind.Number)))
                    throw new FormatException($"位姿事件 {result.Count} 的 {p.Name} 类型无效。");
                if (v.ValueKind == JsonValueKind.Number && (!v.TryGetDouble(out var n) || !double.IsFinite(n))) throw new FormatException("事件数值必须有限。");
                if (v.ValueKind is JsonValueKind.Object or JsonValueKind.Array or JsonValueKind.Undefined) throw new FormatException("事件值必须是标量或 null。");
                fields.Add(p.Name, v.ValueKind == JsonValueKind.Null ? null : v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText());
            }
            result.Add(new ReviewEvent(time, type.GetString()!, fields));
        }
        if (result.Count == 0) throw new FormatException("记录 JSON 没有事件。");
        var recording = new ReviewRecording("real-match-v1", result);
        ExtractPoses(recording);
        return recording;
    }

    private static List<List<string>> ReadCsv(string text, out string header)
    {
        using var reader = new StringReader(text);
        header = reader.ReadLine()?.TrimStart('\uFEFF') ?? throw new FormatException("CSV 文件为空。");
        if (header.Length == 0) throw new FormatException("CSV 文件为空。");
        var rows = new List<List<string>>(); string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var row = new List<string>(); var field = new StringBuilder(); bool quoted = false, closed = false;
            foreach (char c in line)
            {
                if (quoted) { if (c == '"') { quoted = false; closed = true; } else field.Append(c); }
                else if (closed && c == '"') { field.Append('"'); quoted = true; closed = false; }
                else if (c == ',') { row.Add(field.ToString()); field.Clear(); closed = false; }
                else if (c == '"' && field.Length == 0 && !closed) quoted = true;
                else if (closed || c == '"') throw new FormatException("CSV 引号无效。");
                else field.Append(c);
            }
            if (quoted) throw new FormatException("CSV 引号未闭合（不支持跨行字段）。");
            row.Add(field.ToString()); rows.Add(row);
        }
        if (rows.Count == 0) throw new FormatException("CSV 没有数据行。");
        return rows;
    }

    private static double Number(string text, string name)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value)) throw new FormatException(name + "必须是有限数值。");
        return value;
    }
    private static void Finite(double value, string name) { if (!double.IsFinite(value)) throw new ArgumentException(name + "必须是有限数值。"); }
    private static void CheckTime(double time, ref double previous)
    {
        if (!double.IsFinite(time) || time < 0 || time < previous) throw new FormatException("时间必须为非负有限数值且非递减。");
        previous = time;
    }
}
