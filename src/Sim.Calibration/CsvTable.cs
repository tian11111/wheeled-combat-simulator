namespace Sim.Calibration;

/// <summary>
/// Deterministic CSV text parsing shared by the sensor evidence importer.
/// Accepts the raw file TEXT (BOM stripping happens in the caller); throws
/// <see cref="CsvParseException"/> with the source name and 1-based line
/// number. Never enumerates files, reads disk, or writes output.
/// </summary>
public sealed record CsvTable(string Source, string[] Headers, IReadOnlyList<string[]> Rows)
{
    public int IndexOf(string header) => Array.IndexOf(Headers, header);

    public static CsvTable Parse(string source, string text, string[]? requiredHeaders = null)
    {
        var rows = new List<string[]>();
        string[]? headers = null;
        foreach (var (fields, line) in ReadRecords(text))
        {
            if (line == 1)
            {
                if (fields.Length == 0 || string.IsNullOrWhiteSpace(fields[0]))
                {
                    throw new CsvParseException(source, 1, "缺少表头(空首行)");
                }
                headers = fields;
                continue;
            }
            if (headers is null)
            {
                throw new CsvParseException(source, line, "表头缺失");
            }
            if (fields.Length != headers.Length)
            {
                throw new CsvParseException(source, line,
                    $"列数 {fields.Length} 与表头 {headers.Length} 不一致");
            }
            if (fields.All(f => f.Length == 0))
            {
                continue; // skip fully blank trailing line
            }
            rows.Add(fields);
        }
        if (headers is null)
        {
            throw new CsvParseException(source, 1, "文件为空, 无表头");
        }
        if (requiredHeaders is not null && !headers.SequenceEqual(requiredHeaders))
        {
            throw new CsvParseException(source, 1,
                $"表头必须精确为 [{string.Join(",", requiredHeaders)}], 实际 [{string.Join(",", headers)}]");
        }
        return new CsvTable(source, headers, rows);
    }

    /// <summary>Parses a required finite double with origin context.</summary>
    public static double Number(CsvTable table, int row, int column)
    {
        var raw = table.Rows[row][column].Trim();
        if (!double.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value)
            || !double.IsFinite(value))
        {
            throw new CsvParseException(table.Source, row + 2,
                $"列 '{table.Headers[column]}' 值 '{raw}' 不是有限数值");
        }
        return value;
    }

    /// <summary>Yields each record's fields with its 1-based line number.</summary>
    private static IEnumerable<(string[] Fields, int Line)> ReadRecords(string text)
    {
        var fields = new List<string>();
        var field = new System.Text.StringBuilder();
        var quoted = false;
        var line = 1;
        var atFieldStart = true;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (atFieldStart && field.Length == 0 && c == '"')
            {
                quoted = true;
                atFieldStart = false;
                continue;
            }
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    field.Append(c);
                }
                atFieldStart = false;
                continue;
            }
            if (c == '"')
            {
                quoted = true;
                atFieldStart = false;
                continue;
            }
            if (c == ',')
            {
                fields.Add(field.ToString());
                field.Clear();
                atFieldStart = true;
                continue;
            }
            if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }
                fields.Add(field.ToString());
                field.Clear();
                yield return (fields.ToArray(), line);
                fields = new List<string>();
                line++;
                atFieldStart = true;
                continue;
            }
            field.Append(c);
            atFieldStart = false;
        }
        if (field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString());
            yield return (fields.ToArray(), line);
        }
    }
}

/// <summary>CSV-level input failure with file/line origin (exit code 1 material).</summary>
public sealed class CsvParseException : Exception
{
    public CsvParseException(string source, int line, string message)
        : base($"{source}:{line}: {message}")
    {
    }
}
