using Sim.Protocol;

namespace Sim.VisionReplay;

/// <summary>Result of a successful evidence build: import report (pre-fingerprint) + normalized frames.</summary>
public sealed record VisionEvidenceBuildResult(VisionImportReport Report, IReadOnlyList<VisionFrameRecord> Frames);

/// <summary>
/// Offline import/validation of MBri vision CSVs into a vision-replay-v1
/// evidence package. Pure: takes in-memory file bytes, returns the report and
/// normalized frames; writing outputs is the caller's job. Discipline:
/// <list type="bullet">
/// <item>only manifest-named files are consumed; unselected files are reported as ignored,</item>
/// <item>dialects are recognized by exact header-column-set equality; rejected
/// headers carry an explicit missing-column reason,</item>
/// <item>any content violation (sequence/timestamp/order/ranges/status/size)
/// aborts the whole import with file+line context — no silent patching, no
/// truncation, no fabricated detections,</item>
/// <item>per-detection rows are aggregated into frames by receive group
/// (sequence × timestamp × age); re-received duplicates collapse into the
/// first (freshest-age) receive,</item>
/// <item>the report is always groundTruth=false, grade=evidence_only.</item>
/// </list>
/// </summary>
public static class VisionEvidenceBuilder
{
    public static VisionEvidenceBuildResult Build(
        VisionReplayManifest manifest,
        IReadOnlyList<(string Name, byte[] Bytes)> loadedFiles,
        IReadOnlyList<string> ignoredFiles,
        string toolVersion)
    {
        var frames = new List<VisionFrameRecord>();
        var stats = new List<VisionImportFileStat>();
        var rejected = new List<VisionRejection>();

        foreach (var (name, bytes) in loadedFiles.OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            var text = System.Text.Encoding.UTF8.GetString(bytes);
            var table = MbriCsvTable.Parse(name, text);
            var dialect = MbriVisionDialect.Detect(table.Headers);
            if (dialect is null)
            {
                rejected.Add(new VisionRejection { Path = name, Reason = MbriVisionDialect.RejectionReason(table.Headers) });
                continue;
            }
            var (sessionFrames, sessionStat) = ParseSession(name, table, manifest);
            frames.AddRange(sessionFrames);
            stats.Add(sessionStat);
        }

        if (stats.Count == 0)
        {
            throw new VisionEvidenceException(
                "没有可导入的文件: 全部选择文件被拒绝" +
                (rejected.Count > 0 ? " — " + string.Join("; ", rejected.Select(r => $"{r.Path}: {r.Reason}")) : ""));
        }

        var framesBytes = VisionReplayIO.SerializeFrames(frames);
        var evidenceSha256 = VisionReplayIO.Sha256Hex(framesBytes);
        var report = new VisionImportReport
        {
            ToolVersion = toolVersion,
            Label = manifest.Label,
            Source = manifest.Source,
            Model = manifest.Model,
            ClassMapping = new Dictionary<string, string>(manifest.ClassMapping),
            Opponent = manifest.Opponent,
            FrameWidth = manifest.FrameWidth,
            FrameHeight = manifest.FrameHeight,
            TimeBase = manifest.TimeBase,
            Files = stats,
            IgnoredFiles = ignoredFiles.OrderBy(n => n, StringComparer.Ordinal).ToList(),
            RejectedFiles = rejected,
            GroundTruth = false,
            Grade = VisionReplaySchemas.EvidenceOnly,
            EvidenceId = VisionReplayIO.EvidenceId(evidenceSha256),
            EvidenceSha256 = evidenceSha256,
            FramesFile = VisionReplayIO.FramesFileName,
            Limitations =
            [
                "opponent 检测在 MBri YOLO 类别中不存在(IR 接近探测), 证据不含 opponent 帧",
                "label 列为实验名称而非逐帧真值; 本报告恒 groundTruth=false / evidence_only",
                "重收帧(同 sequence 再次入缓存)保留首次(最新鲜)接收的 age/选中目标, 差异计 duplicateReceives",
            ],
        };
        return new VisionEvidenceBuildResult(report, frames);
    }

    // ---------- per-session parsing ----------

    private static (IReadOnlyList<VisionFrameRecord> Frames, VisionImportFileStat Stat) ParseSession(
        string name, MbriCsvTable table, VisionReplayManifest manifest)
    {
        var col = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < table.Headers.Length; i++)
        {
            col[table.Headers[i]] = i;
        }

        var frames = new List<VisionFrameRecord>();
        var statusCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var warmupRows = 0;
        var rows = 0;
        var receiveGroups = 0;
        var detections = 0;
        var duplicateReceives = 0;
        long? currentSequence = null;
        long? previousRowSequence = null;
        double? previousTimestampMs = null;
        double? firstTimestampMs = null;
        double? lastTimestampMs = null;

        // Pending receive group: consecutive rows with equal (sequence, timestamp, age).
        var group = new List<int>();
        (long Sequence, double TimestampMs, double AgeMs)? groupKey = null;

        void FinalizeSequence(long sequence, List<ReceiveGroup> receives)
        {
            receiveGroups += receives.Count;
            if (receives.Count > 1)
            {
                duplicateReceives += receives.Count - 1;
            }
            var first = receives[0];
            for (var i = 1; i < receives.Count; i++)
            {
                var later = receives[i];
                if (later.TimestampMs != first.TimestampMs || later.Status != first.Status
                    || later.Error != first.Error || later.Fps != first.Fps
                    || later.InferenceMs != first.InferenceMs
                    || later.Width != first.Width || later.Height != first.Height
                    || !DetectionsEqual(first.Detections, later.Detections))
                {
                    throw new VisionEvidenceException(
                        $"{name} 行 {later.FirstLine}: sequence {sequence} 的重收帧内容与首次接收不一致");
                }
            }
            frames.Add(new VisionFrameRecord
            {
                Session = name,
                Sequence = sequence,
                TimestampMs = first.TimestampMs,
                ReceivedAgeMs = first.AgeMs,
                Status = first.Status,
                Error = first.Error,
                Fps = first.Fps,
                InferenceMs = first.InferenceMs,
                FrameWidth = first.Width,
                FrameHeight = first.Height,
                DuplicateReceives = receives.Count - 1,
                SelectedTargetIndex = first.SelectedIndex,
                Detections = first.Detections,
            });
            detections += first.Detections.Count;
        }

        var receives = new List<ReceiveGroup>();

        for (var r = 0; r < table.Rows.Count; r++)
        {
            rows++;
            var row = table.Rows[r];
            var status = table.Text(r, col["vision_status"]);
            if (!VisionReplaySchemas.Statuses.Contains(status))
            {
                throw new VisionEvidenceException(
                    $"{name} 行 {row.Line}: vision_status '{status}' 不在允许枚举内");
            }
            statusCounts[status] = statusCounts.GetValueOrDefault(status) + 1;

            var sequenceText = table.Text(r, col["sequence"]);
            if (sequenceText.Length == 0)
            {
                // Warm-up rows before the first frame: no sequence, no timestamp.
                if (status != "no_data_or_stale"
                    || table.Text(r, col["vision_timestamp_ms"]).Length != 0)
                {
                    throw new VisionEvidenceException(
                        $"{name} 行 {row.Line}: 缺少 sequence 的行只允许是 no_data_or_stale 预热行");
                }
                warmupRows++;
                continue;
            }

            if (!long.TryParse(sequenceText, System.Globalization.CultureInfo.InvariantCulture, out var sequence)
                || sequence < 0)
            {
                throw new VisionEvidenceException(
                    $"{name} 行 {row.Line}: sequence '{sequenceText}' 不是非负整数");
            }
            var timestampMs = table.Number(r, col["vision_timestamp_ms"]);
            if (timestampMs < 0)
            {
                throw new VisionEvidenceException(
                    $"{name} 行 {row.Line}: vision_timestamp_ms {timestampMs} 必须非负");
            }
            var ageMs = table.Number(r, col["received_age_ms"]);
            if (ageMs < 0)
            {
                throw new VisionEvidenceException(
                    $"{name} 行 {row.Line}: received_age_ms {ageMs} 必须非负");
            }
            var width = (int)table.Number(r, col["frame_width"]);
            var height = (int)table.Number(r, col["frame_height"]);
            if (width != manifest.FrameWidth || height != manifest.FrameHeight)
            {
                throw new VisionEvidenceException(
                    $"{name} 行 {row.Line}: 帧尺寸 {width}x{height} 与清单 {manifest.FrameWidth}x{manifest.FrameHeight} 不一致");
            }
            if (previousTimestampMs is { } prevTs && timestampMs < prevTs)
            {
                throw new VisionEvidenceException(
                    $"{name} 行 {row.Line}: vision_timestamp_ms {timestampMs} 早于前一行 {prevTs} (时间戳必须单调非降)");
            }
            if (previousRowSequence is { } prevSeq && sequence < prevSeq)
            {
                throw new VisionEvidenceException(
                    $"{name} 行 {row.Line}: sequence {sequence} 小于前一行 sequence {prevSeq} (帧 sequence 必须严格递增且唯一)");
            }

            // Flush the pending receive group BEFORE closing the sequence, so
            // the last group of a sequence is part of its receive list.
            var key = (sequence, timestampMs, ageMs);
            if (groupKey is { } current && (current.Sequence != sequence
                    || current.TimestampMs != timestampMs || current.AgeMs != ageMs))
            {
                receives.Add(FinalizeGroup(name, table, col, groupKey.Value, group, manifest));
                group = [];
            }
            if (currentSequence is { } running && sequence != running)
            {
                FinalizeSequence(running, receives);
                receives = [];
            }
            groupKey = key;
            currentSequence = sequence;
            group.Add(r);
            previousRowSequence = sequence;
            previousTimestampMs = timestampMs;
            firstTimestampMs ??= timestampMs;
            lastTimestampMs = timestampMs;
        }

        if (group.Count > 0 && groupKey is { } finalKey)
        {
            receives.Add(FinalizeGroup(name, table, col, finalKey, group, manifest));
        }
        if (currentSequence is { } lastSequence && receives.Count > 0)
        {
            FinalizeSequence(lastSequence, receives);
        }

        var sessionStat = new VisionImportFileStat
        {
            Path = name,
            Dialect = MbriVisionDialect.HuntDetections,
            Rows = rows,
            WarmupRows = warmupRows,
            ReceiveGroups = receiveGroups,
            Frames = frames.Count,
            Detections = detections,
            DuplicateReceives = duplicateReceives,
            FirstTimestampMs = firstTimestampMs,
            LastTimestampMs = lastTimestampMs,
            StatusCounts = statusCounts,
        };
        return (frames, sessionStat);
    }

    /// <summary>Validates one receive group (identical frame content rows) and normalizes its detections.</summary>
    private static ReceiveGroup FinalizeGroup(
        string name,
        MbriCsvTable table,
        Dictionary<string, int> col,
        (long Sequence, double TimestampMs, double AgeMs) key,
        List<int> rows,
        VisionReplayManifest manifest)
    {
        var firstLine = table.Rows[rows[0]].Line;
        var status = table.Text(rows[0], col["vision_status"]);
        var errorText = table.Text(rows[0], col["vision_error"]);
        var fps = table.OptionalNumber(rows[0], col["fps"]);
        var inferenceMs = table.OptionalNumber(rows[0], col["inference_ms"]);
        var width = (int)table.Number(rows[0], col["frame_width"]);
        var height = (int)table.Number(rows[0], col["frame_height"]);

        // All rows of the group must agree on frame-level content.
        foreach (var r in rows.Skip(1))
        {
            var line = table.Rows[r].Line;
            if (table.Text(r, col["vision_status"]) != status)
            {
                throw new VisionEvidenceException($"{name} 行 {line}: 同一接收组内 vision_status 不一致");
            }
            if (table.Text(r, col["vision_error"]) != errorText)
            {
                throw new VisionEvidenceException($"{name} 行 {line}: 同一接收组内 vision_error 不一致");
            }
            if (table.OptionalNumber(r, col["fps"]) is { } fps2 && (fps is null || Math.Abs(fps2 - fps.Value) > 1e-9))
            {
                throw new VisionEvidenceException($"{name} 行 {line}: 同一接收组内 fps 不一致");
            }
            if (table.OptionalNumber(r, col["inference_ms"]) is { } inf2 && (inferenceMs is null || Math.Abs(inf2 - inferenceMs.Value) > 1e-9))
            {
                throw new VisionEvidenceException($"{name} 行 {line}: 同一接收组内 inference_ms 不一致");
            }
        }

        var count = (int)table.Number(rows[0], col["detection_count"]);
        var detectionIndexes = new List<int>();
        int? selectedIndex = null;
        var detections = new List<VisionFrameDetection>();
        foreach (var r in rows)
        {
            var line = table.Rows[r].Line;
            var indexText = table.Text(r, col["detection_index"]);
            var selectedText = table.Text(r, col["selected_target"]);
            if (selectedText is not ("" or "0" or "1"))
            {
                throw new VisionEvidenceException(
                    $"{name} 行 {line}: selected_target '{selectedText}' 只允许 0/1/空");
            }
            if (count == 0)
            {
                // No-detection group (no_target/no_data_or_stale/error rows).
                if (indexText.Length != 0)
                {
                    throw new VisionEvidenceException(
                        $"{name} 行 {line}: detection_count=0 的组内行不应携带 detection_index '{indexText}'");
                }
                continue;
            }
            if (indexText.Length == 0)
            {
                throw new VisionEvidenceException(
                    $"{name} 行 {line}: detection_count={count} 的接收组内行缺少 detection_index");
            }
            var index = (int)table.Number(r, col["detection_index"]);
            if (index < 0 || index >= count)
            {
                throw new VisionEvidenceException(
                    $"{name} 行 {line}: detection_index {index} 超出 detection_count {count} 范围");
            }
            if (detectionIndexes.Contains(index))
            {
                throw new VisionEvidenceException($"{name} 行 {line}: detection_index {index} 重复");
            }
            detectionIndexes.Add(index);

            if (selectedText == "1")
            {
                if (selectedIndex is not null)
                {
                    throw new VisionEvidenceException($"{name} 行 {line}: 同一接收组内 selected_target 多于一个");
                }
                selectedIndex = index;
            }

            var classId = (int)table.Number(r, col["class_id"]);
            var rawType = table.Text(r, col["target_type"]);
            if (classId is not (0 or 1))
            {
                throw new VisionEvidenceException($"{name} 行 {line}: class_id {classId} 只允许 0(good)/1(bad)");
            }
            if ((classId == 0) != (rawType == "good"))
            {
                throw new VisionEvidenceException(
                    $"{name} 行 {line}: class_id {classId} 与 target_type '{rawType}' 不一致 (0↔good, 1↔bad)");
            }
            if (!manifest.ClassMapping.TryGetValue(rawType, out var label))
            {
                throw new VisionEvidenceException(
                    $"{name} 行 {line}: target_type '{rawType}' 不在清单类别映射内 [{string.Join(",", manifest.ClassMapping.Keys)}]");
            }
            var confidence = table.Number(r, col["confidence"]);
            if (confidence is < 0 or > 1)
            {
                throw new VisionEvidenceException($"{name} 行 {line}: confidence {confidence} 超出 [0,1]");
            }
            var x1 = table.Number(r, col["bbox_x1"]);
            var y1 = table.Number(r, col["bbox_y1"]);
            var x2 = table.Number(r, col["bbox_x2"]);
            var y2 = table.Number(r, col["bbox_y2"]);
            if (x1 < 0 || y1 < 0 || x2 > width || y2 > height)
            {
                throw new VisionEvidenceException(
                    $"{name} 行 {line}: bbox [{x1},{y1},{x2},{y2}] 超出帧范围 {width}x{height}");
            }
            if (x1 >= x2 || y1 >= y2)
            {
                throw new VisionEvidenceException(
                    $"{name} 行 {line}: bbox 必须满足 x1<x2 且 y1<y2, 得到 [{x1},{y1},{x2},{y2}]");
            }
            var centerX = table.Number(r, col["center_x"]);
            var centerY = table.Number(r, col["center_y"]);
            var offsetX = table.Number(r, col["offset_x"]);
            var offsetY = table.Number(r, col["offset_y"]);
            if (offsetX is < -1 or > 1 || offsetY is < -1 or > 1)
            {
                throw new VisionEvidenceException(
                    $"{name} 行 {line}: offset [{offsetX},{offsetY}] 超出 [-1,1]");
            }
            detections.Add(new VisionFrameDetection
            {
                ClassId = classId,
                RawType = rawType,
                Label = label,
                Confidence = confidence,
                Bbox = [x1, y1, x2, y2],
                CenterX = centerX,
                CenterY = centerY,
                OffsetX = offsetX,
                OffsetY = offsetY,
            });
        }

        if (detectionIndexes.Count != count)
        {
            throw new VisionEvidenceException(
                $"{name} 行 {firstLine}: detection_count={count} 与组内检测行数 {detectionIndexes.Count} 不一致");
        }
        if (status == "target" && count == 0)
        {
            throw new VisionEvidenceException($"{name} 行 {firstLine}: vision_status=target 但 detection_count=0");
        }
        if (status is not "target" && count != 0)
        {
            throw new VisionEvidenceException(
                $"{name} 行 {firstLine}: vision_status={status} 但 detection_count={count} (非 target 帧不允许携带检测)");
        }

        return new ReceiveGroup
        {
            FirstLine = firstLine,
            TimestampMs = key.TimestampMs,
            AgeMs = key.AgeMs,
            Status = status,
            Error = errorText.Length == 0 ? null : errorText,
            Fps = fps,
            InferenceMs = inferenceMs,
            Width = width,
            Height = height,
            SelectedIndex = selectedIndex,
            Detections = detections,
        };
    }

    private static bool DetectionsEqual(IReadOnlyList<VisionFrameDetection> a, IReadOnlyList<VisionFrameDetection> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }
        for (var i = 0; i < a.Count; i++)
        {
            var x = a[i];
            var y = b[i];
            if (x.ClassId != y.ClassId || x.RawType != y.RawType || x.Label != y.Label
                || Math.Abs(x.Confidence - y.Confidence) > 1e-9
                || Math.Abs(x.CenterX - y.CenterX) > 1e-9 || Math.Abs(x.CenterY - y.CenterY) > 1e-9
                || Math.Abs(x.OffsetX - y.OffsetX) > 1e-9 || Math.Abs(x.OffsetY - y.OffsetY) > 1e-9)
            {
                return false;
            }
            for (var k = 0; k < 4; k++)
            {
                if (Math.Abs(x.Bbox[k] - y.Bbox[k]) > 1e-9)
                {
                    return false;
                }
            }
        }
        return true;
    }

    private sealed class ReceiveGroup
    {
        public required int FirstLine { get; init; }
        public required double TimestampMs { get; init; }
        public required double AgeMs { get; init; }
        public required string Status { get; init; }
        public required string? Error { get; init; }
        public required double? Fps { get; init; }
        public required double? InferenceMs { get; init; }
        public required int Width { get; init; }
        public required int Height { get; init; }
        public required int? SelectedIndex { get; init; }
        public required List<VisionFrameDetection> Detections { get; init; }
    }
}
