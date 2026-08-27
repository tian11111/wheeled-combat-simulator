using Sim.Protocol;

namespace Sim.Calibration;

/// <summary>One selected raw file with its expected outcome label.</summary>
public sealed record SensorRawSelection
{
    public string File { get; init; } = "";

    /// <summary>gray: unused; front ADC: left/forward/right; shovel: hang/stage.</summary>
    public string? Expect { get; init; }
}

/// <summary>Selection manifest — the ONLY way the importer chooses files.</summary>
public sealed record SensorImportManifest
{
    public string Label { get; init; } = "";
    public double NumericTolerance { get; init; } = 1.0;
    public double RateTolerance { get; init; } = 0.05;

    /// <summary>Tolerance for unitless zone-scale thresholds (gray near-edge).</summary>
    public double ZoneTolerance { get; init; } = 0.02;

    public List<string> GrayRaw { get; init; } = [];
    public string? GrayModel { get; init; }
    public string? GraySummary { get; init; }

    public List<SensorRawSelection> FrontAdcRaw { get; init; } = [];
    public string? FrontAdcModel { get; init; }

    public List<SensorRawSelection> ShovelRaw { get; init; } = [];
    public string? ShovelModel { get; init; }

    public IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(Label))
        {
            yield return "manifest: label (vehicle/session batch) is required.";
        }
        if (GrayModel is null)
        {
            yield return "manifest: gray model file must be selected explicitly.";
        }
        if (GrayRaw.Count == 0)
        {
            yield return "manifest: gray raw list must be non-empty.";
        }
        if (FrontAdcModel is null)
        {
            yield return "manifest: front ADC model file must be selected explicitly.";
        }
        if (FrontAdcRaw.Count == 0)
        {
            yield return "manifest: front ADC raw list must be non-empty.";
        }
        if (ShovelModel is null)
        {
            yield return "manifest: shovel model file must be selected explicitly.";
        }
        if (ShovelRaw.Count == 0)
        {
            yield return "manifest: shovel raw list must be non-empty.";
        }
        foreach (var raw in FrontAdcRaw)
        {
            if (raw.Expect is not ("left" or "right" or "forward"))
            {
                yield return $"manifest: front ADC raw '{raw.File}' needs expect left/forward/right.";
            }
        }
        foreach (var raw in ShovelRaw)
        {
            if (raw.Expect is not ("hang" or "stage"))
            {
                yield return $"manifest: shovel raw '{raw.File}' needs expect hang or stage.";
            }
        }
    }
}

/// <summary>Config.py snapshot values used only for stored-vs-config drift reporting.</summary>
public sealed record SensorConfigSnapshot(
    double? GrayNearEdgeEnter,
    double? FrontRatioThreshold,
    double? FrontSignalMin,
    double? ShovelHangEnter,
    double? ShovelHangClear);

/// <summary>
/// Pure builder: takes the manifest, the decoded text of exactly the selected
/// files (relative path → text) and the full listing of the data directory,
/// and produces the validated sensor-calibration-v1 report. No IO, no clock
/// (generatedAt is injected by the caller).
/// </summary>
public static class SensorEvidenceBuilder
{
    private static readonly string[] GrayRawHeaders = ["t", "front", "rear", "left", "right"];
    private static readonly string[] AdcRawHeaders = ["t", "left", "right", "diff", "valid"];
    private static readonly string[] ShovelRawHeaders = ["t", "left", "right", "valid"];
    private static readonly string[] ModelHeaders = ["parameter", "value", "source", "note"];

    public static SensorCalibrationReport Build(
        SensorImportManifest manifest,
        IReadOnlyDictionary<string, Dictionary<string, (byte[] Bytes, string Sha256)>> loaded,
        IEnumerable<string> dataDirCsvListing,
        SensorConfigSnapshot? config)
    {
        var errors = manifest.Validate().ToList();
        if (errors.Count > 0)
        {
            throw new SensorEvidenceException(string.Join(" ", errors));
        }
        var files = new List<SensorCalibrationFile>();
        var rejected = new List<SensorCalibrationRejection>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        var limitations = new List<string>
        {
            "灰度原始数据无 x/y/th 坐标 (coordinateData=false), 不能构造 FieldModel.GrayGridMap 场地灰度网格。",
            "stored 模型/重算/config 差异只报告不合并; 无自动取最新策略。",
            "本产物为离线证据, 不进入 Scenario/Snapshot/回放; 运行时集成需另立任务。",
        };

        // ---- gray ----
        var grayTable = RequireTable(loaded, manifest.GrayModel!, "gray_model", files, used, rejected);
        var gray = ReadGrayModel(grayTable);
        var grayRawTables = new List<(string File, CsvTable Table, SensorReplay.GrayRow[] Rows)>();
        foreach (var grayRawFile in manifest.GrayRaw)
        {
            var table = LoadRaw(loaded, grayRawFile, GrayRawHeaders, "gray_raw", files, used, rejected);
            if (table is null)
            {
                continue;
            }
            var rows = GrayRows(table);
            grayRawTables.Add((grayRawFile, table, rows));
        }
        var grayReplay = grayRawTables.Count > 0
            ? CombineGray(grayRawTables, gray)
            : null;

        // ---- front ADC ----
        var frontTable = RequireTable(loaded, manifest.FrontAdcModel!, "front_adc_model", files, used, rejected);
        var frontKv = ReadKeyValueModel(frontTable);
        var front = new FrontAdcModel
        {
            FilterWindow = (int)Require(frontKv, "filter_window"),
            SignalMin = Require(frontKv, "signal_min"),
            DiffLow = Require(frontKv, "diff_low"),
            DiffHigh = Require(frontKv, "diff_high"),
            RatioThreshold = config?.FrontRatioThreshold is { } rt && rt > 0 ? rt : null,
        };
        var frontReplays = new List<(string File, string Expect, SensorReplay.AdcReplayResult R)>();
        foreach (var raw in manifest.FrontAdcRaw)
        {
            var table = LoadRaw(loaded, raw.File, AdcRawHeaders, "front_adc_raw", files, used, rejected);
            if (table is null)
            {
                continue;
            }
            frontReplays.Add((raw.File, raw.Expect!, SensorReplay.ReplayFrontAdc(front, AdcRows(table), raw.Expect!)));
        }

        // ---- shovel ----
        var shovelTable = RequireTable(loaded, manifest.ShovelModel!, "shovel_model", files, used, rejected);
        var shovelKv = ReadKeyValueModel(shovelTable);
        var shovel = new ShovelModel
        {
            FilterWindow = 9,
            HangEnter = Require(shovelKv, "hang_enter"),
            HangClear = Require(shovelKv, "hang_clear"),
        };
        var shovelReplays = new List<(string File, string Expect, SensorReplay.ShovelReplayResult R)>();
        foreach (var raw in manifest.ShovelRaw)
        {
            var table = LoadRaw(loaded, raw.File, ShovelRawHeaders, "shovel_raw", files, used, rejected);
            if (table is null)
            {
                continue;
            }
            shovelReplays.Add((raw.File, raw.Expect!, SensorReplay.ReplayShovel(shovel, ShovelRows(table))));
        }

        // ---- ignored (not selected) ----
        var ignored = dataDirCsvListing
            .Where(name => !used.Contains(name) && !name.Equals("config.py", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        // ---- recomputation + drift comparison ----
        var comparison = new List<CalibrationDelta>();
        var grayReasons = new List<string>();
        // Gray cannot be recomputed without group labels → config drift only.
        if (config?.GrayNearEdgeEnter is { } cfgEnter)
        {
            var stored = gray.Channels[0].NearEdgeEnter;
            comparison.Add(Delta("gray", "near_edge_enter", stored, null, cfgEnter, manifest.ZoneTolerance));
            if (Math.Abs(stored - cfgEnter) > manifest.ZoneTolerance)
            {
                grayReasons.Add($"near_edge_enter stored {stored} vs config {cfgEnter} 不一致");
            }
        }
        else
        {
            grayReasons.Add("未提供 config 快照; gray 重算因缺少组标签不可行 (stored 值未独立验证)。");
        }

        var frontReasons = new List<string>();
        double? leftP95 = null, rightP05 = null, centerP05 = null, centerP95 = null;
        foreach (var (file, expect, r) in frontReplays)
        {
            switch (expect)
            {
                case "left":
                    leftP95 = Math.Max(leftP95 ?? double.NegativeInfinity, r.DiffP95);
                    break;
                case "right":
                    rightP05 = Math.Min(rightP05 ?? double.PositiveInfinity, r.DiffP05);
                    break;
                case "forward":
                    // Band statistics only use center files whose raw signal
                    // median clears the stored floor (far-wall runs must not widen it).
                    if (r.SignalP50 >= front.SignalMin || r.SignalP01 >= front.SignalMin)
                    {
                        centerP05 = Math.Min(centerP05 ?? double.PositiveInfinity, r.DiffP05);
                        centerP95 = Math.Max(centerP95 ?? double.NegativeInfinity, r.DiffP95);
                    }
                    break;
            }
        }
        if (leftP95 is not null && centerP05 is not null)
        {
            var recomputedLow = (leftP95.Value + centerP05.Value) / 2;
            comparison.Add(Delta("frontAdc", "diff_low", front.DiffLow, recomputedLow, null, manifest.NumericTolerance));
            if (Math.Abs(front.DiffLow - recomputedLow) > manifest.NumericTolerance)
            {
                frontReasons.Add($"diff_low stored {Math.Round(front.DiffLow, 3)} vs 重算 {Math.Round(recomputedLow, 3)} 超出容差");
            }
        }
        if (centerP95 is not null && rightP05 is not null)
        {
            var recomputedHigh = (centerP95.Value + rightP05.Value) / 2;
            comparison.Add(Delta("frontAdc", "diff_high", front.DiffHigh, recomputedHigh, null, manifest.NumericTolerance));
            if (Math.Abs(front.DiffHigh - recomputedHigh) > manifest.NumericTolerance)
            {
                frontReasons.Add($"diff_high stored {Math.Round(front.DiffHigh, 3)} vs 重算 {Math.Round(recomputedHigh, 3)} 超出容差");
            }
        }
        if (config is { FrontRatioThreshold: { } ratioCfg })
        {
            comparison.Add(Delta("frontAdc", "ratio_threshold", null, front.RatioThreshold, ratioCfg, manifest.NumericTolerance));
        }

        var shovelReasons = new List<string>();
        double? stageMinP99 = null, hangMinP01 = null, stageMaxP99 = null, hangMaxP01 = null;
        foreach (var (file, expect, r) in shovelReplays)
        {
            if (expect == "stage")
            {
                stageMinP99 = Math.Max(stageMinP99 ?? double.NegativeInfinity, r.MinP99);
                stageMaxP99 = Math.Max(stageMaxP99 ?? double.NegativeInfinity, r.MaxP99);
            }
            else
            {
                hangMinP01 = Math.Min(hangMinP01 ?? double.PositiveInfinity, r.MinP01);
                hangMaxP01 = Math.Min(hangMaxP01 ?? double.PositiveInfinity, r.MaxP01);
            }
        }
        if (stageMinP99 is not null && hangMinP01 is not null)
        {
            var enter = (stageMinP99.Value + hangMinP01.Value) / 2;
            comparison.Add(Delta("shovel", "hang_enter", shovel.HangEnter, enter, config?.ShovelHangEnter, 1.0));
            if (Math.Abs(shovel.HangEnter - enter) > manifest.NumericTolerance)
            {
                shovelReasons.Add($"hang_enter stored {shovel.HangEnter} vs 重算 {Math.Round(enter, 1)} 超出容差");
            }
        }
        if (stageMaxP99 is not null && hangMaxP01 is not null)
        {
            var clear = (stageMaxP99.Value + hangMaxP01.Value) / 2;
            comparison.Add(Delta("shovel", "hang_clear", shovel.HangClear, clear, config?.ShovelHangClear, 1.0));
            if (Math.Abs(shovel.HangClear - clear) > manifest.NumericTolerance)
            {
                shovelReasons.Add($"hang_clear stored {shovel.HangClear} vs 重算 {Math.Round(clear, 1)} 超出容差");
            }
        }

        // ---- gates + blocks ----
        var frontFailFiles = frontReplays
            .Where(fr => fr.R.ReadyRows == 0 || (double)fr.R.Mismatches / Math.Max(1, fr.R.LabeledRows) > manifest.RateTolerance)
            .Select(fr => fr.File).ToList();
        var shovelOk = shovelReplays.Count > 0 && shovelReplays.All(sr =>
            sr.R.ReadyRows > 0 &&
            (sr.Expect == "hang" ? sr.R.HangAsserts / (double)sr.R.ReadyRows >= 0.3 : sr.R.HangAsserts == 0));

        var grayReplayMetrics = grayReplay is null ? null : Metrics(
            grayReplay.Value.Total, grayReplay.Value.Invalid, grayReplay.Value.Ready,
            grayReplay.Value.First, grayReplay.Value.Last,
            new Dictionary<string, int>
            {
                ["near_edge"] = grayReplay.Value.NearEdge,
                ["white_hit"] = grayReplay.Value.WhiteHits,
            },
            new(), grayReplay.Value.Invalid <= Math.Max(1, grayReplay.Value.Total / 100));
        var frontMetrics = frontReplays.Count == 0 ? null : Metrics(
            frontReplays.Sum(f => f.R.TotalRows), frontReplays.Sum(f => f.R.InvalidRows),
            frontReplays.Sum(f => f.R.ReadyRows),
            frontReplays.Min(f => f.R.FirstT), frontReplays.Max(f => f.R.LastT),
            frontReplays.SelectMany(f => f.R.DirectionCounts)
                .GroupBy(kv => kv.Key).ToDictionary(g => g.Key, g => g.Sum(kv => kv.Value)),
            frontFailFiles, frontFailFiles.Count == 0);
        var shovelMetrics = shovelReplays.Count == 0 ? null : Metrics(
            shovelReplays.Sum(s => s.R.TotalRows), shovelReplays.Sum(s => s.R.InvalidRows),
            shovelReplays.Sum(s => s.R.ReadyRows),
            shovelReplays.Min(s => s.R.FirstT), shovelReplays.Max(s => s.R.LastT),
            new Dictionary<string, int>
            {
                ["hang"] = shovelReplays.Sum(s => s.R.HangAsserts),
                ["hang_transitions"] = shovelReplays.Sum(s => s.R.HangTransitions),
                ["clear_transitions"] = shovelReplays.Sum(s => s.R.ClearTransitions),
            },
            shovelReplays.Where(s => !Passes(s)).Select(s => s.File).ToList(),
            shovelOk && shovelReplays.All(s => s.R.ReadyRows > 0));

        static bool Passes((string File, string Expect, SensorReplay.ShovelReplayResult R) s)
            => s.R.ReadyRows > 0 && (s.Expect == "hang"
                ? s.R.HangAsserts / (double)s.R.ReadyRows >= 0.3
                : s.R.HangAsserts == 0);

        var blocks = new List<SensorCalibrationModelBlock>
        {
            new()
            {
                Model = "gray",
                Status = grayReplayMetrics is { Passed: false } ? SensorCalibrationStatus.Rejected : SensorCalibrationStatus.EvidenceOnly,
                Reason = JoinReason(grayReasons, grayReplayMetrics is { Passed: false } ? "灰度回放门控未过" : null),
                SourceFiles = files.Where(f => f.Role is "gray_model" or "gray_raw").Select(f => f.Path).ToList(),
                Replay = grayReplayMetrics,
                Limitations = ["无坐标数据, 不能构造场地灰度网格; 不可重算 (缺组标签), 仅 stored vs config 漂移可见。"],
                RuntimeCandidate = grayReplayMetrics is { Passed: true } && grayReasons.Count == 0,
            },
            new()
            {
                Model = "frontAdc",
                Status = frontMetrics is { Passed: false } ? SensorCalibrationStatus.Rejected : SensorCalibrationStatus.EvidenceOnly,
                Reason = JoinReason(frontReasons, frontMetrics is { Passed: false } ? $"回放方向误判文件: {string.Join(",", frontFailFiles)}" : null),
                SourceFiles = files.Where(f => f.Role is "front_adc_model" or "front_adc_raw").Select(f => f.Path).ToList(),
                Replay = frontMetrics,
                Limitations = ["stored 绝对差带与生产 ratio 模型是两套语义, 均如实呈现, 不自动替换。"],
                RuntimeCandidate = frontMetrics is { Passed: true } && frontReasons.Count == 0,
            },
            new()
            {
                Model = "shovel",
                Status = shovelMetrics is { Passed: false } ? SensorCalibrationStatus.Rejected : SensorCalibrationStatus.EvidenceOnly,
                Reason = JoinReason(shovelReasons, shovelMetrics is { Passed: false } ? "悬空/收回回放门控未过" : null),
                SourceFiles = files.Where(f => f.Role is "shovel_model" or "shovel_raw").Select(f => f.Path).ToList(),
                Replay = shovelMetrics,
                Limitations = ["回放仅判定滤波悬空信号与迟滞, 不模拟电机倒车计时。"],
                RuntimeCandidate = shovelMetrics is { Passed: true } && shovelReasons.Count == 0,
            },
        };

        var report = new SensorCalibrationReport
        {
            Schema = ProtocolVersion.SensorCalibrationFormat,
            ToolVersion = "sensor-calibration-v1",
            Label = manifest.Label,
            Files = files.OrderBy(f => f.Path, StringComparer.Ordinal).ToList(),
            IgnoredFiles = ignored,
            RejectedFiles = rejected,
            Gray = gray,
            FrontAdc = front,
            Shovel = shovel,
            Blocks = blocks,
            Comparison = comparison,
            BatchConsistent = comparison.All(d => d.Consistent),
            Limitations = limitations,
        };
        var validation = report.Validate().ToList();
        if (validation.Count > 0)
        {
            throw new SensorEvidenceException($"内部错误: 生成报告未通过自校验: {string.Join(" ", validation)}");
        }
        return report;
    }

    // ---------- helpers ----------

    private static string? JoinReason(List<string> reasons, string? gate)
    {
        var all = reasons.Where(r => !string.IsNullOrWhiteSpace(r)).ToList();
        if (gate is not null)
        {
            all.Insert(0, gate);
        }
        return all.Count == 0 ? null : string.Join("; ", all);
    }

    private static ReplayMetrics Metrics(
        int total, int invalid, int ready, double? firstT, double? lastT,
        Dictionary<string, int> decisions, List<string> failed, bool passed)
        => new()
        {
            Samples = ready,
            InvalidRows = invalid,
            FirstT = firstT,
            LastT = lastT,
            DecisionCounts = decisions,
            FailedFiles = failed,
            Passed = passed,
        };

    private static CalibrationDelta Delta(string model, string field, double? stored, double? recomputed, double? config, double tol)
    {
        var deltas = new[] { stored, recomputed, config }.Where(v => v is not null).Select(v => v!.Value).ToList();
        var spread = deltas.Count >= 2 ? deltas.Max() - deltas.Min() : 0.0;
        return new CalibrationDelta
        {
            Model = model,
            Field = field,
            Stored = stored is null ? null : Math.Round(stored.Value, 3),
            Recomputed = recomputed is null ? null : Math.Round(recomputed.Value, 3),
            Config = config is null ? null : Math.Round(config.Value, 3),
            MaxDelta = Math.Round(spread, 3),
            Consistent = spread <= Math.Max(tol, 1e-9),
        };
    }

    private static CsvTable RequireTable(
        IReadOnlyDictionary<string, Dictionary<string, (byte[] Bytes, string Sha256)>> loaded,
        string file, string role,
        List<SensorCalibrationFile> files, HashSet<string> used, List<SensorCalibrationRejection> rejected)
    {
        var table = LoadRaw(loaded, file, null, role, files, used, rejected)
            ?? throw new SensorEvidenceException($"模型文件 {file} 不可用 (见 rejected 列表)");
        if (!table.Headers.SequenceEqual(ModelHeaders) && !table.Headers[0].Equals("sensor", StringComparison.Ordinal))
        {
            throw new SensorEvidenceException($"{file}: 模型表头既不是 parameter,value,source,note 也不是 sensor 通道表");
        }
        return table;
    }

    private static CsvTable? LoadRaw(
        IReadOnlyDictionary<string, Dictionary<string, (byte[] Bytes, string Sha256)>> loaded,
        string file, string[]? exactHeaders, string role,
        List<SensorCalibrationFile> files, HashSet<string> used, List<SensorCalibrationRejection> rejected)
    {
        (byte[] Bytes, string Sha256)? found = null;
        foreach (var (_, dict) in loaded)
        {
            if (dict.TryGetValue(file, out var entry))
            {
                found = entry;
                break;
            }
        }
        if (found is null)
        {
            rejected.Add(new SensorCalibrationRejection { Path = file, Reason = "选择清单引用但数据目录中不存在" });
            return null;
        }
        var (bytes, sha) = found.Value;
        string text;
        try
        {
            text = new System.Text.UTF8Encoding(false, true).GetString(bytes).TrimStart('﻿');
        }
        catch (ArgumentException)
        {
            rejected.Add(new SensorCalibrationRejection { Path = file, Reason = "非 UTF-8 编码" });
            return null;
        }
        CsvTable table;
        try
        {
            table = CsvTable.Parse(file, text, exactHeaders);
        }
        catch (CsvParseException ex)
        {
            rejected.Add(new SensorCalibrationRejection { Path = file, Reason = ex.Message });
            return null;
        }
        files.Add(new SensorCalibrationFile
        {
            Path = file,
            Role = role,
            Sha256 = sha,
            Bytes = bytes.LongLength,
        });
        used.Add(file);
        return table;
    }

    private static GrayModelData ReadGrayModel(CsvTable table)
    {
        var col = new Dictionary<string, int>
        {
            ["sensor"] = table.IndexOf("sensor"),
            ["filter_window"] = table.IndexOf("filter_window"),
            ["near_edge_enter"] = table.IndexOf("near_edge_enter"),
            ["near_edge_clear"] = table.IndexOf("near_edge_clear"),
            ["edge_reference"] = table.IndexOf("edge_reference"),
            ["center_reference"] = table.IndexOf("center_reference"),
            ["white_reference"] = table.IndexOf("white_reference"),
            ["safe_upper"] = table.IndexOf("safe_upper"),
            ["white_lower"] = table.IndexOf("white_lower"),
            ["white_enter"] = table.IndexOf("white_enter"),
            ["white_clear"] = table.IndexOf("white_clear"),
        };
        if (col.Values.Any(v => v < 0))
        {
            throw new SensorEvidenceException($"{table.Source}: 灰度模型缺少必需列");
        }
        var channels = new List<GrayChannelModel>();
        for (var r = 0; r < table.Rows.Count; r++)
        {
            var row = table.Rows[r];
            if (string.IsNullOrWhiteSpace(row[col["sensor"]]))
            {
                continue;
            }
            channels.Add(new GrayChannelModel
            {
                Sensor = row[col["sensor"]].Trim(),
                FilterWindow = (int)CsvTable.Number(table, r, col["filter_window"]),
                NearEdgeEnter = CsvTable.Number(table, r, col["near_edge_enter"]),
                NearEdgeClear = CsvTable.Number(table, r, col["near_edge_clear"]),
                EdgeReference = CsvTable.Number(table, r, col["edge_reference"]),
                CenterReference = CsvTable.Number(table, r, col["center_reference"]),
                WhiteReference = CsvTable.Number(table, r, col["white_reference"]),
                SafeUpper = CsvTable.Number(table, r, col["safe_upper"]),
                WhiteLower = CsvTable.Number(table, r, col["white_lower"]),
                WhiteEnter = CsvTable.Number(table, r, col["white_enter"]),
                WhiteClear = CsvTable.Number(table, r, col["white_clear"]),
            });
        }
        var gray = new GrayModelData { Channels = channels, CoordinateData = false };
        var errors = gray.Validate().ToList();
        if (errors.Count > 0)
        {
            throw new SensorEvidenceException($"{table.Source}: {string.Join(" ", errors)}");
        }
        return gray;
    }

    private static Dictionary<string, double> ReadKeyValueModel(CsvTable table)
    {
        var kv = new Dictionary<string, double>(StringComparer.Ordinal);
        var keyCol = table.IndexOf("parameter");
        var valCol = table.IndexOf("value");
        if (keyCol < 0 || valCol < 0)
        {
            throw new SensorEvidenceException($"{table.Source}: 模型表头需含 parameter,value 列");
        }
        for (var r = 0; r < table.Rows.Count; r++)
        {
            var key = table.Rows[r][keyCol].Trim();
            if (key.Length == 0)
            {
                continue;
            }
            kv[key] = CsvTable.Number(table, r, valCol);
        }
        return kv;
    }

    private static double Require(Dictionary<string, double> kv, string key)
        => kv.TryGetValue(key, out var value)
            ? value
            : throw new SensorEvidenceException($"模型缺少参数 '{key}'");

    private static SensorReplay.GrayRow[] GrayRows(CsvTable table)
    {
        var rows = new List<SensorReplay.GrayRow>();
        var ci = new[] { "t", "front", "rear", "left", "right" }.Select(table.IndexOf).ToArray();
        for (var r = 0; r < table.Rows.Count; r++)
        {
            var valid = true;
            var t = Safe(table, r, ci[0], ref valid);
            rows.Add(new SensorReplay.GrayRow(t,
                [Safe(table, r, ci[1], ref valid), Safe(table, r, ci[2], ref valid),
                 Safe(table, r, ci[3], ref valid), Safe(table, r, ci[4], ref valid)],
                valid));
        }
        return rows.ToArray();
    }

    private static SensorReplay.AdcRow[] AdcRows(CsvTable table)
    {
        var rows = new List<SensorReplay.AdcRow>();
        var ci = new[] { "t", "left", "right", "valid" }.Select(table.IndexOf).ToArray();
        for (var r = 0; r < table.Rows.Count; r++)
        {
            var valid = IsOne(table, r, ci[3]);
            rows.Add(new SensorReplay.AdcRow(
                Safe(table, r, ci[0], ref valid), Safe(table, r, ci[1], ref valid),
                Safe(table, r, ci[2], ref valid), valid));
        }
        return rows.ToArray();
    }

    private static SensorReplay.ShovelRow[] ShovelRows(CsvTable table)
    {
        var rows = new List<SensorReplay.ShovelRow>();
        var ci = new[] { "t", "left", "right", "valid" }.Select(table.IndexOf).ToArray();
        for (var r = 0; r < table.Rows.Count; r++)
        {
            var valid = IsOne(table, r, ci[3]);
            rows.Add(new SensorReplay.ShovelRow(
                Safe(table, r, ci[0], ref valid), Safe(table, r, ci[1], ref valid),
                Safe(table, r, ci[2], ref valid), valid));
        }
        return rows.ToArray();
    }

    private static double Safe(CsvTable table, int row, int col, ref bool valid)
    {
        try
        {
            return CsvTable.Number(table, row, col);
        }
        catch (CsvParseException)
        {
            valid = false;
            return 0;
        }
    }

    private static bool IsOne(CsvTable table, int row, int col)
    {
        var raw = table.Rows[row][col].Trim();
        return raw is "1" || raw.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private static (int Total, int Invalid, int Ready, double? First, double? Last, int NearEdge, int WhiteHits)? CombineGray(
        List<(string File, CsvTable Table, SensorReplay.GrayRow[] Rows)> rawTables, GrayModelData model)
    {
        var totals = 0;
        var invalid = 0;
        var ready = 0;
        var near = 0;
        var white = 0;
        double? first = null, last = null;
        foreach (var (_, _, rows) in rawTables)
        {
            var r = SensorReplay.ReplayGray(model, rows);
            totals += r.TotalRows;
            invalid += r.InvalidRows;
            ready += r.ReadyRows;
            near += r.NearEdgeAsserts;
            white += r.WhiteHitRows;
            first ??= r.FirstT;
            last = r.LastT ?? last;
        }
        return (totals, invalid, ready, first, last, near, white);
    }
}

/// <summary>Validation failure for sensor-evidence inputs (exit code 1 material).</summary>
public sealed class SensorEvidenceException : Exception
{
    public SensorEvidenceException(string message) : base(message)
    {
    }
}
