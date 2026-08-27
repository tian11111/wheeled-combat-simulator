using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

using Sim.Calibration;
using Sim.Protocol;

namespace Sim.Cli;

/// <summary>
/// `calibrate` — offline telemetry → parameter report → optional scenario emit
/// → optional fidelity promotion. Exit codes: 0 success, 1 input/fitting/
/// IO error, 2 usage. Validation runs once up front; invalid telemetry never
/// produces a report, patch or fidelity update (PRD AC1/R2).
/// </summary>
public static class CalibrateCommand
{
    private sealed class CalibrateOptions
    {
        public string? Input { get; init; }
        public string? Out { get; set; }
        public string? VehicleId { get; init; }
        public string? BaseScenario { get; init; }
        public string? EmitScenario { get; init; }
        public string? FidelityPath { get; init; }
        public bool UpdateFidelity { get; init; }
        public bool Force { get; init; }
    }

    public static int Run(string[] args)
    {
        var options = Parse(args);
        if (options.Input is null)
        {
            Console.Error.WriteLine("calibrate requires --input <telemetry.json>");
            return 2;
        }
        if (!File.Exists(options.Input))
        {
            Console.Error.WriteLine($"找不到遥测文件: {options.Input}");
            return 1;
        }

        var raw = File.ReadAllBytes(options.Input);
        TelemetryFile telemetry;
        try
        {
            telemetry = ProtocolJson.Deserialize<TelemetryFile>(System.Text.Encoding.UTF8.GetString(raw));
        }
        catch (System.Text.Json.JsonException ex)
        {
            Console.Error.WriteLine($"遥测 JSON 解析失败: {ex.Message}");
            return 1;
        }
        var errors = telemetry.Validate().ToList();
        if (errors.Count > 0)
        {
            foreach (var error in errors)
            {
                Console.Error.WriteLine($"telemetry: {error}");
            }
            Console.Error.WriteLine($"遥测校验失败 ({errors.Count} 项), 未生成任何报告或 patch。");
            return 1;
        }

        var vehicleId = options.VehicleId ?? telemetry.Vehicle.Id;
        var inputSha = CalibrationMath.Sha256Hex(raw);
        var report = ReportWriter.Fingerprint(
            Calibrator.Calibrate(telemetry, inputSha),
            DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));

        if (options.Out is null)
        {
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            options.Out = Path.Combine("calibration", $"{Sanitize(vehicleId)}-{stamp}.json");
        }
        var outPath = Path.GetFullPath(options.Out);
        if (File.Exists(outPath) && !options.Force)
        {
            Console.Error.WriteLine($"输出文件已存在: {outPath} (覆盖请加 --force)");
            return 1;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        File.WriteAllText(outPath, ReportWriter.Serialize(report));
        Console.WriteLine($"标定报告: {outPath} (contentSha256={report.ContentSha256?[..16]}…, inputSha256={inputSha[..16]}…)");
        PrintFits(report);

        if (options.EmitScenario is { } scenarioPath)
        {
            var baseScenario = options.BaseScenario is null
                ? new Scenario { Seed = 42, Blocks = OfficialLayout.Blocks }
                : ProtocolJson.Deserialize<Scenario>(File.ReadAllText(options.BaseScenario));
            try
            {
                var patched = ReportWriter.ApplyPatch(baseScenario, report);
                ReportWriter.EmitScenario(scenarioPath, patched);
                Console.WriteLine($"已应用标定 patch 的新场景: {Path.GetFullPath(scenarioPath)} (官方场景与旧回放不受影响)");
            }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine($"--emit-scenario 失败: {ex.Message}");
                return 1;
            }
        }

        if (options.UpdateFidelity)
        {
            var fidelityPath = Path.GetFullPath(options.FidelityPath ?? "fidelity.json");
            var updated = UpdateFidelity(fidelityPath, report, outPath, options.Force);
            Console.WriteLine(updated.Count > 0
                ? $"保真度已更新: {string.Join(", ", updated)}"
                : "保真度未改动 (没有满足留出验证 + 真实数据条件的子系统)。");
        }
        else
        {
            Console.WriteLine("保真度未改动; 人工复核报告后用 --update-fidelity 显式登记。");
        }
        return 0;
    }

    private static void PrintFits(CalibrationReport report)
    {
        foreach (var (name, fit) in report.Fits)
        {
            if (!fit.Calibrated)
            {
                Console.WriteLine($"  {name}: 不可标定 — {fit.Reason}");
                continue;
            }
            var holdout = fit.HoldoutRmse is { } hr
                ? $"RMSE {hr}"
                : fit.HoldoutAccuracy is { } ha
                    ? $"acc {ha}"
                    : "-";
            Console.WriteLine($"  {name} = {fit.Value} (拟合 {fit.FitSamples} 样本 RMSE {fit.FitRmse}; 留出 {fit.HoldoutSamples} 样本 {holdout}; 晋升 {(fit.Eligible ? "可" : "否")}{(fit.Reason is null ? "" : $" [{fit.Reason}]")})");
        }
        var mount = report.Mount;
        if (mount is not null)
        {
            Console.WriteLine($"  mount: 拟合 {mount.FitTrials} 试验 (正确 {mount.FitCorrect}), 留出 {mount.HoldoutTrials} 试验 (错误率 {mount.HoldoutErrorRate?.ToString("P1") ?? "-"}){(mount.Reason is null ? "" : $" — {mount.Reason}")}");
        }
    }

    /// <summary>
    /// Promote eligible subsystems in a fidelity file. Only writes entries for
    /// subsystems whose holdout metrics passed on real data (PRD R6).
    /// </summary>
    public static List<string> UpdateFidelity(string fidelityPath, CalibrationReport report, string reportPath, bool force)
    {
        if (!File.Exists(fidelityPath))
        {
            throw new FileNotFoundException($"找不到 fidelity 文件: {fidelityPath}");
        }
        var root = JsonNode.Parse(File.ReadAllText(fidelityPath))
            ?? throw new InvalidOperationException("fidelity.json 不是合法 JSON 对象");
        var subsystems = root["subsystems"]?.AsObject()
            ?? throw new InvalidOperationException("fidelity.json 缺少 subsystems 对象");
        var promoted = new (string Name, bool Eligible, string[] Parameters)[]
        {
            ("friction", report.Eligibility.Friction, ["latFrictionK", "BLOCK_MU_K"]),
            ("collision", report.Eligibility.Collision, ["angDamping", "COLLISION_RESTITUTION"]),
            ("stall", report.Eligibility.Stall, ["STALL_SPEED"]),
            ("mount", report.Eligibility.Mount, ["MOUNT_V_MIN", "MOUNT_ANGLE_MAX"]),
        };
        var updated = new List<string>();
        foreach (var (name, eligible, parameters) in promoted)
        {
            if (!eligible)
            {
                continue;
            }
            var entry = subsystems[name]?.AsObject()
                ?? throw new InvalidOperationException($"fidelity.json 缺少 subsystem '{name}'");
            entry["status"] = "calibrated";
            entry["label"] = "已标定";
            entry["parameters"] = new JsonArray(parameters.Select(s => JsonValue.Create(s)!).ToArray());
            entry["calibratedAt"] = report.GeneratedAt;
            entry["evidence"] =
                $"sim-cli calibrate: {reportPath}; 遥测 SHA-256: {report.Telemetry.Sha256[..16]}…;"
                + $" 车辆 {report.Telemetry.VehicleId} 场地 {report.Telemetry.Date}。留出集达标后方可采用; 仍需固定 seed 与真机复测。";
            updated.Add(name);
        }
        if (updated.Count > 0 || force)
        {
            root["updatedAt"] = report.GeneratedAt;
            root["lastCalibration"] = new JsonObject
            {
                ["output"] = reportPath,
                ["telemetrySha256"] = report.Telemetry.Sha256,
                ["updatedSubsystems"] = new JsonArray(updated.Select(s => JsonValue.Create(s)!).ToArray()),
            };
            File.WriteAllText(fidelityPath, root.ToJsonString(new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }) + "\n");
        }
        return updated;
    }

    private static string Sanitize(string value)
        => string.Concat(value.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_')).Trim('_') is { Length: > 0 } s ? s : "vehicle";

    private static CalibrateOptions Parse(string[] args)
    {
        string? Get(string key)
        {
            var index = Array.IndexOf(args, key);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }
        return new CalibrateOptions
        {
            Input = Get("--input"),
            Out = Get("--out"),
            VehicleId = Get("--vehicle-id"),
            BaseScenario = Get("--base-scenario"),
            EmitScenario = Get("--emit-scenario"),
            FidelityPath = Get("--fidelity"),
            UpdateFidelity = args.Contains("--update-fidelity"),
            Force = args.Contains("--force"),
        };
    }
}
