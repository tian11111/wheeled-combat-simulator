using System.Text;
using Sim.Cli;
using Sim.Protocol;
using Sim.VisionReplay;

namespace Sim.Tests;

/// <summary>
/// vision-replay-v1 import chain tests: dialect detection by exact header set,
/// the full per-file validation matrix (violations abort with file+line and
/// produce NO output), receive-group aggregation, and the honest
/// groundTruth=false / evidence_only grade.
/// </summary>
public class VisionReplayImportTests : IDisposable
{
    private const string MiniFixtureDir = "src/Sim.Tests/fixtures/mbri-vision-mini";
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (var dir in _tempDirs.Where(Directory.Exists))
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // best-effort temp cleanup
            }
        }
    }

    // ---------- synthetic hunt-dialect CSV helpers ----------

    private static Dictionary<string, string> BaseRow(
        long seq, double tsMs, double ageMs, string status, string rawType = "good",
        int classId = 0, double confidence = 0.9, string selected = "1",
        int width = 640, int height = 480, int count = 1, int index = 0,
        double fps = 5.4, double inferenceMs = 180.0, string error = "", double t = 1.0)
        => new()
        {
            ["t"] = t.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture),
            ["sequence"] = seq.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["vision_timestamp_ms"] = tsMs.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
            ["received_age_ms"] = ageMs.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
            ["vision_status"] = status,
            ["vision_error"] = error,
            ["frame_width"] = width.ToString(),
            ["frame_height"] = height.ToString(),
            ["fps"] = fps.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture),
            ["inference_ms"] = inferenceMs.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture),
            ["detection_count"] = count.ToString(),
            ["detection_index"] = count == 0 ? "" : index.ToString(),
            ["selected_target"] = selected,
            ["class_id"] = classId.ToString(),
            ["target_type"] = rawType,
            ["confidence"] = confidence.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture),
            ["bbox_x1"] = "100",
            ["bbox_y1"] = "100",
            ["bbox_x2"] = "260",
            ["bbox_y2"] = "260",
            ["center_x"] = "180",
            ["center_y"] = "180",
            ["offset_x"] = "-0.4375",
            ["offset_y"] = "-0.25",
        };

    private static Dictionary<string, string> With(Dictionary<string, string> row, params (string Key, string Value)[] changes)
    {
        var clone = new Dictionary<string, string>(row);
        foreach (var (key, value) in changes)
        {
            clone[key] = value;
        }
        return clone;
    }

    private static string HuntCsv(params Dictionary<string, string>[] rows)
    {
        var headers = MbriVisionDialect.HuntDetectionColumns;
        var builder = new StringBuilder(string.Join(",", headers));
        foreach (var row in rows)
        {
            builder.Append('\n');
            builder.Append(string.Join(",", headers.Select(h => row.TryGetValue(h, out var value) ? value : "")));
        }
        return builder.ToString();
    }

    /// <summary>Valid minimal session: warmup row, target(good), no_target, target(bad).</summary>
    private static string ValidCsv() => HuntCsv(
        With(BaseRow(0, 0, 0, "no_data_or_stale", count: 0, t: 0.01),
            ("sequence", ""), ("vision_timestamp_ms", ""), ("received_age_ms", "")),
        BaseRow(1, 1000, 12.5, "target"),
        BaseRow(2, 1200, 8.0, "no_target", count: 0, selected: ""),
        BaseRow(3, 1400, 9.5, "target", rawType: "bad", classId: 1));

    private static VisionEvidenceBuildResult Build(params (string Name, string Csv)[] files)
    {
        var loaded = files
            .Select(f => (f.Name, Encoding.UTF8.GetBytes(f.Csv)))
            .ToList();
        var manifest = new VisionReplayManifest
        {
            Label = "test-batch",
            Model = new VisionModelRef { Name = "yolo-test" },
            ClassMapping = new Dictionary<string, string> { ["good"] = "buff", ["bad"] = "debuff" },
            Opponent = "unavailable(ir-probe)",
            FrameWidth = 640,
            FrameHeight = 480,
            TimeBase = "epoch ms",
            Files = loaded.Select(f => new VisionReplayFileSelection
            {
                Path = f.Name,
                Sha256 = VisionReplayIO.Sha256Hex(f.Item2),
                Bytes = f.Item2.Length,
            }).ToList(),
        };
        return VisionEvidenceBuilder.Build(manifest, loaded, [], "test");
    }

    // ---------- happy path ----------

    [Fact]
    public void ValidCsv_AggregatesReceiveGroupsIntoFrames()
    {
        var result = Build(("a.csv", ValidCsv()));
        var frames = result.Frames;
        Assert.Equal(3, frames.Count);
        Assert.All(frames, f => Assert.Equal("a.csv", f.Session));

        var target = frames[0];
        Assert.Equal(1, target.Sequence);
        Assert.Equal("target", target.Status);
        Assert.Single(target.Detections);
        Assert.Equal("good", target.Detections[0].RawType);
        Assert.Equal("buff", target.Detections[0].Label);
        Assert.Equal(0, target.SelectedTargetIndex);
        Assert.Equal(new double[] { 100, 100, 260, 260 }, target.Detections[0].Bbox);
        Assert.Equal(12.5, target.ReceivedAgeMs);
        Assert.Equal(0, target.DuplicateReceives);

        Assert.Equal("no_target", frames[1].Status);
        Assert.Empty(frames[1].Detections);
        Assert.Null(frames[1].SelectedTargetIndex);
        Assert.Equal("debuff", frames[2].Detections[0].Label);
        Assert.Equal(1, frames[2].Detections[0].ClassId);
    }

    [Fact]
    public void MultiDetectionRows_AggregateIntoOneFrame()
    {
        var r0 = BaseRow(7, 500, 3, "target", count: 3, index: 0);
        var r1 = BaseRow(7, 500, 3, "target", rawType: "bad", classId: 1, confidence: 0.4, count: 3, index: 1, selected: "0");
        var r2 = BaseRow(7, 500, 3, "target", rawType: "bad", classId: 1, confidence: 0.3, count: 3, index: 2, selected: "0");
        var result = Build(("a.csv", HuntCsv(r0, r1, r2)));
        var frame = Assert.Single(result.Frames);
        Assert.Equal(3, frame.Detections.Count);
        Assert.Equal("buff", frame.Detections[0].Label);
        Assert.Equal("debuff", frame.Detections[1].Label);
        Assert.Equal(0, frame.SelectedTargetIndex);
        Assert.Equal(3, result.Report.Files[0].Detections);
    }

    [Fact]
    public void ReReceivedSequence_CollapsesIntoFirstReceive()
    {
        // Same sequence re-logged later with a larger age: identical payload,
        // different selection on the stale copy. The FIRST (freshest) receive
        // wins; the duplicate is counted, not rejected.
        var first = BaseRow(9, 700, 5, "target", count: 2, index: 0);
        var firstOther = BaseRow(9, 700, 5, "target", rawType: "bad", classId: 1, confidence: 0.3, count: 2, index: 1, selected: "0");
        var second = With(BaseRow(9, 700, 250, "target", count: 2, index: 0), ("selected_target", "0"), ("t", "1.2"));
        var secondOther = With(BaseRow(9, 700, 250, "target", rawType: "bad", classId: 1, confidence: 0.3, count: 2, index: 1),
            ("selected_target", "1"), ("t", "1.2"));
        var result = Build(("a.csv", HuntCsv(first, firstOther, second, secondOther)));
        var frame = Assert.Single(result.Frames);
        Assert.Equal(1, frame.DuplicateReceives);
        Assert.Equal(5, frame.ReceivedAgeMs);
        Assert.Equal(0, frame.SelectedTargetIndex); // first receive's selection
        Assert.Equal(2, frame.Detections.Count);
    }

    [Fact]
    public void Report_IsHonestEvidenceOnly()
    {
        var result = Build(("a.csv", ValidCsv()));
        var report = result.Report;
        Assert.False(report.GroundTruth);
        Assert.Equal("evidence_only", report.Grade);
        Assert.Equal("vision-replay-v1", report.Schema);
        Assert.Equal("unavailable(ir-probe)", report.Opponent);
        Assert.Empty(report.Validate());
        Assert.Multiple(
            () => Assert.Equal(1, report.Files[0].WarmupRows),
            () => Assert.Equal(3, report.Files[0].Frames),
            () => Assert.Equal(4, report.Files[0].Rows),
            () => Assert.Equal(1000, report.Files[0].FirstTimestampMs),
            () => Assert.Equal(1400, report.Files[0].LastTimestampMs));
    }

    [Fact]
    public void EvidenceHash_IsContentDeterministic()
    {
        var a = Build(("a.csv", ValidCsv()));
        var b = Build(("a.csv", ValidCsv()));
        Assert.Equal(a.Report.EvidenceSha256, b.Report.EvidenceSha256);
        Assert.Equal(a.Report.EvidenceId, b.Report.EvidenceId);
        Assert.StartsWith("vr-", a.Report.EvidenceId);
    }

    // ---------- dialect detection ----------

    [Fact]
    public void SimplifiedDialect_IsRejectedWithMissingColumnReason()
    {
        var simplified = "t,vision_sequence,vision_status,left_cmd,right_cmd\n1,stale,0,0,0\n";
        var result = Build(("a.csv", ValidCsv()), ("b.csv", simplified));
        var rejection = Assert.Single(result.Report.RejectedFiles);
        Assert.Equal("b.csv", rejection.Path);
        Assert.Contains("缺少必需视觉列", rejection.Reason);
        Assert.Contains("detection_index", rejection.Reason);
        Assert.Single(result.Report.Files);
        Assert.Equal(3, result.Frames.Count);
    }

    [Fact]
    public void UnknownHeader_IsRejectedNotGuessed()
    {
        var weird = "t,a,b\n1,2,3\n";
        var result = Build(("a.csv", ValidCsv()), ("b.csv", weird));
        Assert.Single(result.Report.Files);
        var rejection = Assert.Single(result.Report.RejectedFiles);
        Assert.Contains("不匹配", rejection.Reason);
    }

    [Fact]
    public void ColumnOrderIsIrrelevant_SetEqualityDetectsDialect()
    {
        var headers = MbriVisionDialect.HuntDetectionColumns.ToArray();
        Array.Reverse(headers);
        var csv = string.Join(",", headers) + "\n" + string.Join(",", Enumerable.Repeat("", headers.Length));
        var table = MbriCsvTable.Parse("a.csv", csv);
        Assert.NotNull(MbriVisionDialect.Detect(table.Headers));
    }

    [Fact]
    public void AllRejectedFiles_AbortTheImport()
    {
        var ex = Assert.Throws<VisionEvidenceException>(() => Build(("a.csv", "t,a\n1,2\n")));
        Assert.Contains("没有可导入的文件", ex.Message);
    }

    [Fact]
    public void StructuralCsvErrors_ReportLineNumbers()
    {
        var headers = MbriVisionDialect.HuntDetectionColumns;
        var broken = string.Join(",", headers) + "\n1,2\n";
        var ex = Assert.Throws<MbriCsvException>(() => Build(("a.csv", broken)));
        Assert.Contains("行 2", ex.Message);
    }

    // ---------- validation matrix (each violation aborts with file+line) ----------

    [Fact]
    public void Violations_AbortWithFileAndLine()
    {
        var cases = new Dictionary<string, Dictionary<string, string>[]>
        {
            ["sequence_backwards"] =
            [
                BaseRow(2, 1000, 1, "target"),
                BaseRow(1, 1100, 1, "target"),
            ],
            ["timestamp_backwards"] =
            [
                BaseRow(1, 1200, 1, "target"),
                BaseRow(2, 1100, 1, "target"),
            ],
            ["frame_size_mismatch"] =
            [
                BaseRow(1, 1000, 1, "target", width: 1280, height: 720),
            ],
            ["bad_status"] =
            [
                BaseRow(1, 1000, 1, "mostly_fine"),
            ],
            ["confidence_out_of_range"] =
            [
                BaseRow(1, 1000, 1, "target", confidence: 1.4),
            ],
            ["bbox_degenerate"] =
            [
                With(BaseRow(1, 1000, 1, "target"), ("bbox_x1", "260"), ("bbox_x2", "100")),
            ],
            ["bbox_outside_frame"] =
            [
                With(BaseRow(1, 1000, 1, "target"), ("bbox_y2", "481")),
            ],
            ["offset_out_of_range"] =
            [
                With(BaseRow(1, 1000, 1, "target"), ("offset_x", "1.5")),
            ],
            ["negative_timestamp"] =
            [
                With(BaseRow(1, 1000, 1, "target"), ("vision_timestamp_ms", "-100")),
            ],
            ["class_type_mismatch"] =
            [
                BaseRow(1, 1000, 1, "target", classId: 0, rawType: "bad"),
            ],
            ["unknown_raw_type"] =
            [
                BaseRow(1, 1000, 1, "target", classId: 0, rawType: "enemy"),
            ],
            ["detection_count_mismatch"] =
            [
                With(BaseRow(1, 1000, 1, "target"), ("detection_count", "2")),
            ],
            ["target_without_detections"] =
            [
                With(BaseRow(1, 1000, 1, "target"), ("detection_count", "0"), ("detection_index", "")),
            ],
            ["non_target_with_detections"] =
            [
                BaseRow(1, 1000, 1, "no_target"),
            ],
        };
        var didNotAbort = new List<string>();
        foreach (var (name, rows) in cases)
        {
            var csv = HuntCsv(rows);
            try
            {
                Build(("a.csv", csv));
                didNotAbort.Add(name);
            }
            catch (VisionEvidenceException ex)
            {
                Assert.True(ex.Message.Contains("a.csv", StringComparison.Ordinal), $"{name}: {ex.Message}");
                Assert.Contains("行 ", ex.Message);
            }
        }
        Assert.True(didNotAbort.Count == 0, $"cases that did not abort: {string.Join(",", didNotAbort)}");
    }

    [Fact]
    public void TwoSelectedTargets_InOneGroup_Aborts()
    {
        var r0 = BaseRow(7, 500, 3, "target", count: 2, index: 0);
        var r1 = BaseRow(7, 500, 3, "target", rawType: "bad", classId: 1, confidence: 0.4, count: 2, index: 1);
        var ex = Assert.Throws<VisionEvidenceException>(() => Build(("a.csv", HuntCsv(r0, r1))));
        Assert.Contains("selected_target 多于一个", ex.Message);
    }

    [Fact]
    public void ReReceiveWithDifferentPayload_Aborts()
    {
        var first = BaseRow(9, 700, 5, "target", index: 0);
        var second = With(BaseRow(9, 700, 250, "target", index: 0), ("confidence", "0.5"), ("t", "1.2"));
        var ex = Assert.Throws<VisionEvidenceException>(() => Build(("a.csv", HuntCsv(first, second))));
        Assert.Contains("重收帧内容与首次接收不一致", ex.Message);
    }

    [Fact]
    public void WarmupRows_RequireNoDataOrStaleWithoutTimestamp()
    {
        var bad = With(BaseRow(0, 0, 0, "no_data_or_stale", count: 0), ("sequence", ""), ("vision_timestamp_ms", "999"));
        var ex = Assert.Throws<VisionEvidenceException>(() => Build(("a.csv", HuntCsv(bad))));
        Assert.Contains("预热行", ex.Message);
    }

    [Fact]
    public void ManifestValidation_RequiresExplicitMapping()
    {
        var manifest = new VisionReplayManifest { Label = "x", FrameWidth = 640, FrameHeight = 480 };
        Assert.Contains(manifest.Validate(), e => e.Contains("classMapping"));
        var wrongMapping = new VisionReplayManifest
        {
            Label = "x",
            FrameWidth = 640,
            FrameHeight = 480,
            ClassMapping = new Dictionary<string, string> { ["good"] = "opponent" },
        };
        Assert.Contains(wrongMapping.Validate(), e => e.Contains("good→buff"));
    }

    // ---------- CLI level over the vendored real-data fixture ----------

    private string CopyMini()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, MiniFixtureDir)))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        var temp = Path.Combine(Path.GetTempPath(), $"mbrivision-{Guid.NewGuid():N}");
        _tempDirs.Add(temp);
        Directory.CreateDirectory(temp);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(dir!.FullName, MiniFixtureDir)))
        {
            File.Copy(file, Path.Combine(temp, Path.GetFileName(file)));
        }
        return temp;
    }

    private static int RunImportCli(string dir, string outPath)
        => Program.Main(
        [
            "vision", "import",
            "--manifest", Path.Combine(dir, "selection.manifest.json"),
            "--evidence-out", Path.Combine(dir, "evidence"),
            "--out", outPath,
            "--force",
        ]);

    [Fact]
    public void MiniImport_SucceedsAndWritesEvidencePackage()
    {
        var dir = CopyMini();
        var outPath = Path.Combine(dir, "out", "import-report.json");
        Assert.Equal(0, RunImportCli(dir, outPath));
        var report = ProtocolJson.Deserialize<VisionImportReport>(File.ReadAllText(outPath));
        Assert.Empty(report.Validate());
        Assert.Equal(2, report.Files.Count);
        Assert.False(report.GroundTruth);
        Assert.True(File.Exists(Path.Combine(dir, "evidence", VisionReplayIO.FramesFileName)));
        Assert.True(File.Exists(Path.Combine(dir, "evidence", VisionReplayIO.ImportReportFileName)));

        // The evidence package hash matches the frames.jsonl bytes on disk.
        var framesSha = VisionReplayIO.Sha256Hex(
            File.ReadAllBytes(Path.Combine(dir, "evidence", VisionReplayIO.FramesFileName)));
        Assert.Equal(report.EvidenceSha256, framesSha);
        var frames = VisionReplayIO.ParseFrames(
            File.ReadAllText(Path.Combine(dir, "evidence", VisionReplayIO.FramesFileName)));
        Assert.Equal(2, frames.Select(f => f.Session).Distinct().Count());
    }

    [Fact]
    public void MiniImport_IsDeterministicAndPathIndependent()
    {
        var dirA = CopyMini();
        var dirB = CopyMini();
        var outA = Path.Combine(dirA, "out", "report.json");
        var outB = Path.Combine(dirB, "out", "report.json");
        Assert.Equal(0, RunImportCli(dirA, outA));
        Assert.Equal(0, RunImportCli(dirB, outB));
        var reportA = ProtocolJson.Deserialize<VisionImportReport>(File.ReadAllText(outA));
        var reportB = ProtocolJson.Deserialize<VisionImportReport>(File.ReadAllText(outB));
        Assert.Equal(reportA.ContentSha256, reportB.ContentSha256);
        Assert.Equal(reportA.EvidenceSha256, reportB.EvidenceSha256);
        // Absolute paths never leak into the report.
        var json = File.ReadAllText(outA);
        Assert.DoesNotContain(dirA, json, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.GetTempPath(), json, StringComparison.Ordinal);
    }

    [Fact]
    public void Import_HashMismatch_FailsWithZeroOutput()
    {
        var dir = CopyMini();
        var victim = Path.Combine(dir, "hunt_drive_20260817_095205.csv");
        File.WriteAllText(victim, File.ReadAllText(victim) + "\n");
        var outPath = Path.Combine(dir, "out", "report.json");
        var exit = RunImportCli(dir, outPath);
        Assert.Equal(1, exit);
        Assert.False(File.Exists(outPath));
        Assert.False(Directory.Exists(Path.Combine(dir, "evidence")));
    }

    [Fact]
    public void Import_ValidationViolation_FailsWithZeroOutput()
    {
        // Manifest hash recomputed over a corrupted CSV, so the failure is
        // the validator's, not the hash check's.
        var dir = CopyMini();
        var headers = MbriVisionDialect.HuntDetectionColumns;
        var corruptRow = BaseRow(1, 1000, 5, "no_target", count: 0, width: 999, height: 999);
        File.WriteAllText(Path.Combine(dir, "corrupt.csv"), HuntCsv(corruptRow));
        var manifestText = File.ReadAllText(Path.Combine(dir, "selection.manifest.json"));
        var manifest = ProtocolJson.Deserialize<VisionReplayManifest>(manifestText);
        var bytes = File.ReadAllBytes(Path.Combine(dir, "corrupt.csv"));
        manifest = manifest with
        {
            Files =
            [
                .. manifest.Files,
                new VisionReplayFileSelection
                {
                    Path = "corrupt.csv",
                    Sha256 = VisionReplayIO.Sha256Hex(bytes),
                    Bytes = bytes.Length,
                },
            ],
        };
        File.WriteAllText(Path.Combine(dir, "selection.manifest.json"), ProtocolJson.Serialize(manifest));
        var outPath = Path.Combine(dir, "out", "report.json");
        var exit = RunImportCli(dir, outPath);
        Assert.Equal(1, exit);
        Assert.False(File.Exists(outPath));
        Assert.False(Directory.Exists(Path.Combine(dir, "evidence")));
    }

    [Fact]
    public void Import_MissingArgs_Exit2()
    {
        Assert.Equal(2, Program.Main(["vision"]));
        Assert.Equal(2, Program.Main(["vision", "import"]));
        Assert.Equal(2, Program.Main(["vision", "evaluate"]));
        Assert.Equal(2, Program.Main(["vision", "wat"]));
    }
}
