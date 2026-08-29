using System.Security.Cryptography;
using System.Text;
using Sim.Protocol;

namespace Sim.VisionReplay;

/// <summary>Thrown for hard validation/IO failures of the vision evidence line; the CLI maps it to exit 1.</summary>
public sealed class VisionEvidenceException : Exception
{
    public VisionEvidenceException(string message) : base(message)
    {
    }
}

/// <summary>
/// Canonical serialization + content fingerprints + atomic writes for the
/// vision evidence line. Fingerprints are computed over the report with the
/// volatile bookkeeping (generatedAt, contentSha256 itself) removed, so the
/// same input bytes always produce the same hash. The frames JSONL uses one
/// compact <see cref="VisionFrameRecord"/> per line (LF endings, no BOM).
/// </summary>
public static class VisionReplayIO
{
    /// <summary>Artifact name of the normalized frames inside an evidence directory.</summary>
    public const string FramesFileName = "frames.jsonl";

    /// <summary>Artifact name of the archived import report inside an evidence directory.</summary>
    public const string ImportReportFileName = "import-report.json";

    public static string Sha256Hex(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static string Sha256Hex(string text) => Sha256Hex(Encoding.UTF8.GetBytes(text));

    /// <summary>Stable evidence id derived from the package content hash.</summary>
    public static string EvidenceId(string evidenceSha256) => $"vr-{evidenceSha256[..16]}";

    /// <summary>Serialize with the content fingerprint set (generatedAt excluded from the hash).</summary>
    public static VisionImportReport Fingerprint(VisionImportReport report, string? generatedAt)
    {
        var content = report with { GeneratedAt = null, ContentSha256 = null };
        var hash = Sha256Hex(ProtocolJson.Serialize(content));
        return report with { GeneratedAt = generatedAt, ContentSha256 = hash };
    }

    /// <summary>Serialize with the content fingerprint set (generatedAt excluded from the hash).</summary>
    public static VisionReplayReport Fingerprint(VisionReplayReport report, string? generatedAt)
    {
        var content = report with { GeneratedAt = null, ContentSha256 = null };
        var hash = Sha256Hex(ProtocolJson.Serialize(content));
        return report with { GeneratedAt = generatedAt, ContentSha256 = hash };
    }

    /// <summary>Serializes frames to the canonical JSONL byte form (one compact object per line, LF endings).</summary>
    public static byte[] SerializeFrames(IEnumerable<VisionFrameRecord> frames)
    {
        var builder = new StringBuilder();
        foreach (var frame in frames)
        {
            builder.Append(ProtocolJson.Serialize(frame));
            builder.Append('\n');
        }
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    /// <summary>Parses a frames JSONL payload (produced by <see cref="SerializeFrames"/>).</summary>
    public static List<VisionFrameRecord> ParseFrames(string text)
    {
        var frames = new List<VisionFrameRecord>();
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Length == 0)
            {
                if (i == lines.Length - 1)
                {
                    continue; // trailing newline
                }
                throw new VisionEvidenceException($"frames.jsonl 行 {i + 1}: 空行");
            }
            VisionFrameRecord frame;
            try
            {
                frame = ProtocolJson.Deserialize<VisionFrameRecord>(line);
            }
            catch (System.Text.Json.JsonException ex)
            {
                throw new VisionEvidenceException($"frames.jsonl 行 {i + 1}: JSON 解析失败 — {ex.Message}");
            }
            var errors = frame.Validate().ToList();
            if (errors.Count > 0)
            {
                throw new VisionEvidenceException($"frames.jsonl 行 {i + 1}: {string.Join(" ", errors)}");
            }
            frames.Add(frame);
        }
        if (frames.Count == 0)
        {
            throw new VisionEvidenceException("frames.jsonl: 未包含任何帧记录");
        }
        return frames;
    }

    /// <summary>Atomic write (temp + move) so a crash never leaves a partial artifact.</summary>
    public static void WriteAtomically(string path, byte[] bytes)
    {
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        var temp = full + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, full, overwrite: true);
    }

    /// <summary>Atomic UTF-8 (no BOM) text write (temp + move).</summary>
    public static void WriteAtomically(string path, string text)
        => WriteAtomically(path, Encoding.UTF8.GetBytes(text));
}
