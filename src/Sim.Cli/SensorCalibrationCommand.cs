using System.Security.Cryptography;
using Sim.Calibration;
using Sim.Protocol;

namespace Sim.Cli;

/// <summary>
/// `sensor-calibration import` — offline MBri evidence pipeline.
/// Validates manifest + every selected file fully before creating any output;
/// writes report atomically. Exit codes: 0 report written (evidence_only
/// allowed), 1 validation/IO failure (no output), 2 usage.
/// </summary>
public static class SensorCalibrationCommand
{
    private const long MaxFileBytes = 128L * 1024 * 1024;

    public static int Run(string[] args)
    {
        if (args.Length < 2 || !string.Equals(args[1], "import", StringComparison.Ordinal))
        {
            Console.Error.WriteLine("用法: sensor-calibration import --data-dir <path> --manifest <json> --out <report.json> [--config config.py] [--force]");
            return 2;
        }
        string? Get(string key)
        {
            var index = Array.IndexOf(args, key);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }
        var dataDir = Get("--data-dir");
        var manifestPath = Get("--manifest");
        var outPath = Get("--out");
        var configPath = Get("--config");
        var force = args.Contains("--force");
        if (dataDir is null || manifestPath is null || outPath is null)
        {
            Console.Error.WriteLine("缺少 --data-dir / --manifest / --out");
            return 2;
        }
        try
        {
            return Execute(dataDir, manifestPath, outPath, configPath, force);
        }
        catch (SensorEvidenceException ex)
        {
            Console.Error.WriteLine($"sensor-calibration: {ex.Message}");
            return 1;
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"sensor-calibration IO: {ex.Message}");
            return 1;
        }
        catch (System.Text.Json.JsonException ex)
        {
            Console.Error.WriteLine($"sensor-calibration JSON: {ex.Message}");
            return 1;
        }
    }

    private static int Execute(string dataDir, string manifestPath, string outArg, string? configPath, bool force)
    {
        if (!Directory.Exists(dataDir))
        {
            throw new SensorEvidenceException($"数据目录不存在: {dataDir}");
        }
        var manifest = ProtocolJson.Deserialize<SensorImportManifest>(File.ReadAllText(manifestPath));
        var manifestErrors = manifest.Validate().ToList();
        if (manifestErrors.Count > 0)
        {
            throw new SensorEvidenceException(string.Join(" ", manifestErrors));
        }
        var root = Path.GetFullPath(dataDir);
        var allCsv = Directory.EnumerateFiles(root, "*.csv", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(n => n is not null)
            .Cast<string>()
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        var selected = manifest.GrayRaw
            .Concat([manifest.GrayModel ?? ""])
            .Concat(manifest.FrontAdcRaw.Select(r => r.File))
            .Concat([manifest.FrontAdcModel ?? ""])
            .Concat(manifest.ShovelRaw.Select(r => r.File))
            .Concat([manifest.ShovelModel ?? ""])
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // Pre-flight: every selected file must exist and fit size limits BEFORE output.
        foreach (var name in selected)
        {
            if (!allCsv.Contains(name, StringComparer.Ordinal))
            {
                throw new SensorEvidenceException($"选择文件不存在于数据目录: {name}");
            }
            var size = new FileInfo(Path.Combine(root, name)).Length;
            if (size > MaxFileBytes)
            {
                throw new SensorEvidenceException($"文件超过 128MB 上限: {name} ({size} B)");
            }
        }

        var loaded = new Dictionary<string, (byte[] Bytes, string Sha256)>(StringComparer.Ordinal);
        foreach (var name in selected)
        {
            var bytes = File.ReadAllBytes(Path.Combine(root, name));
            loaded[name] = (bytes, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        }
        var groups = new Dictionary<string, Dictionary<string, (byte[], string)>>
        {
            ["data"] = loaded.ToDictionary(kv => kv.Key, kv => (kv.Value.Bytes, kv.Value.Sha256)),
        };
        SensorConfigSnapshot? config = configPath is null
            ? null
            : ConfigSnapshot.Parse(File.ReadAllText(configPath));

        var report = SensorEvidenceBuilder.Build(
            manifest, groups, allCsv, config);
        report = SensorEvidence.Fingerprint(
            report, DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));

        var outPath = Path.GetFullPath(outArg);
        if (File.Exists(outPath) && !force)
        {
            throw new SensorEvidenceException($"输出已存在 (覆盖需 --force): {outPath}");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        var temp = outPath + ".tmp";
        File.WriteAllText(temp, SensorEvidence.Serialize(report));
        File.Move(temp, outPath, overwrite: true);

        Console.WriteLine($"传感器标定证据: {outPath}");
        Console.WriteLine($"  contentSha256={report.ContentSha256?[..16]}… 批次={report.Label}");
        foreach (var block in report.Blocks)
        {
            var replayText = block.Replay is { } r
                ? $" (就绪 {r.Samples}, 无效 {r.InvalidRows}, 门控 {(r.Passed ? "过" : "不过")}{(r.FailedFiles.Count > 0 ? $", 失败文件 {string.Join(",", r.FailedFiles)}" : "")})"
                : "";
            Console.WriteLine($"  {block.Model}: {block.Status}{replayText} 运行时候选={(block.RuntimeCandidate ? "是" : "否")}"
                + (block.Reason is { } reason ? $" — {reason}" : ""));
        }
        Console.WriteLine($"  使用 {report.Files.Count} / 忽略 {report.IgnoredFiles.Count} / 拒绝 {report.RejectedFiles.Count} 文件; 批次一致={report.BatchConsistent}");
        return 0;
    }
}
