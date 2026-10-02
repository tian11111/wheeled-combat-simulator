using System.Text.Json;

// Minimal JSONL stdio controller fixture for batch tests. One mode per launch:
//   echo     — reply {"v":0.05,"w":0.1} echoing obs.requestId (healthy path)
//   wrongid  — echo requestId+1000 (far-future id: the bridge must drop every
//              action; the offset is large enough that a late reply can never
//              alias a future frame's request id, so fault counts are exact)
//   bad      — reply a non-JSON line per tick (bridge drops it, deadline fault)
//   die      — exit immediately (dead process: zero-action fallback + faults)
//   hang     — consumes stdin but never replies and never exits (proves batch
//              reaps controller processes; reading keeps the bridge write side
//              from blocking so the batch can finish and dispose its bridges)
//   utf8probe— decode stdin as UTF-8 (independent of console codepage) and reply
//              v = robot.action 字符数, w = 首个码点 (桥的 UTF-8 契约探针)
//   rlguard  — 展演注入守卫: 只有携带 11 维有限 rlObservation 的帧才回零动作;
//              缺字段/维度不符/非有限 ⇒ 立即退出, 让桥在下一次 Decide 计 fault。
//              于是 "展演 e2e 断言 faults==0" 就钉住了 C# 侧真的注入了 rlObservation;
//              非展演路径(无 rlObservation)下该模式会 fault, 可做反向对照证明守卫非空转。
var mode = args.Length > 0 ? args[0] : "echo";
switch (mode)
{
    case "die":
        return;
    case "rlguard":
    {
        string? guardLine;
        while ((guardLine = Console.In.ReadLine()) is not null)
        {
            var valid = false;
            long? guardId = null;
            try
            {
                using var document = JsonDocument.Parse(guardLine);
                var root = document.RootElement;
                if (root.TryGetProperty("rlObservation", out var rl)
                    && rl.ValueKind == JsonValueKind.Array
                    && rl.GetArrayLength() == 11)
                {
                    valid = true;
                    foreach (var value in rl.EnumerateArray())
                    {
                        if (value.ValueKind != JsonValueKind.Number
                            || !value.TryGetDouble(out var number)
                            || !double.IsFinite(number))
                        {
                            valid = false;
                            break;
                        }
                    }
                }
                if (root.TryGetProperty("requestId", out var id)
                    && id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var parsed))
                {
                    guardId = parsed;
                }
            }
            catch (JsonException)
            {
                valid = false;
            }
            if (!valid)
            {
                return; // 注入缺失/坏帧: 响亮失败, 由桥计 fault 而不是静默发动作
            }
            Console.WriteLine(guardId is null
                ? "{\"v\":0.0,\"w\":0.0}"
                : $"{{\"v\":0.0,\"w\":0.0,\"requestId\":{guardId}}}");
            Console.Out.Flush();
        }
        return;
    }
    case "hang":
        while (Console.In.ReadLine() is not null)
        {
            // absorb observations; never answer
        }
        return;
    case "utf8probe":
        using (var utf8In = new StreamReader(Console.OpenStandardInput(), new System.Text.UTF8Encoding(false)))
        {
            string? probeLine;
            while ((probeLine = utf8In.ReadLine()) is not null)
            {
                var action = "";
                long? probeId = null;
                try
                {
                    using var document = JsonDocument.Parse(probeLine);
                    if (document.RootElement.TryGetProperty("robot", out var robot)
                        && robot.TryGetProperty("action", out var label)
                        && label.ValueKind == JsonValueKind.String)
                    {
                        action = label.GetString() ?? "";
                    }
                    if (document.RootElement.TryGetProperty("requestId", out var id)
                        && id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var parsed))
                    {
                        probeId = parsed;
                    }
                }
                catch (JsonException)
                {
                    // reply with the zero-length measurement; the bridge still matches by id
                }
                var firstCodePoint = action.Length == 0 ? 0 : char.ConvertToUtf32(action, 0);
                Console.WriteLine(probeId is null
                    ? $"{{\"v\":{action.Length},\"w\":{firstCodePoint}}}"
                    : $"{{\"v\":{action.Length},\"w\":{firstCodePoint},\"requestId\":{probeId}}}");
                Console.Out.Flush();
            }
        }
        return;
}

string? line;
while ((line = Console.In.ReadLine()) is not null)
{
    if (mode == "bad")
    {
        Console.WriteLine("not json at all");
    }
    else
    {
        long? requestId = null;
        try
        {
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.TryGetProperty("requestId", out var id)
                && id.ValueKind == JsonValueKind.Number
                && id.TryGetInt64(out var parsed))
            {
                requestId = mode == "wrongid" ? parsed + 1000 : parsed;
            }
        }
        catch (JsonException)
        {
            // fall through: reply without requestId (legacy-accepted)
        }
        Console.WriteLine(requestId is null
            ? "{\"v\":0.05,\"w\":0.1}"
            : $"{{\"v\":0.05,\"w\":0.1,\"requestId\":{requestId}}}");
    }
    Console.Out.Flush();
}
