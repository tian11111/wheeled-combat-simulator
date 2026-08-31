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
var mode = args.Length > 0 ? args[0] : "echo";
switch (mode)
{
    case "die":
        return;
    case "hang":
        while (Console.In.ReadLine() is not null)
        {
            // absorb observations; never answer
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
