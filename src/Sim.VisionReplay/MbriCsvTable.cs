namespace Sim.VisionReplay;

/// <summary>CSV structural failure with source name and 1-based line number.</summary>
public sealed class MbriCsvException : Exception
{
    public MbriCsvException(string source, int line, string message)
        : base($"{source} 行 {line}: {message}")
    {
    }
}

/// <summary>
/// Deterministic CSV text parsing for the MBri vision importer. Accepts the
/// raw file TEXT (BOM stripping happens in the caller); throws
/// <see cref="MbriCsvException"/> with the source name and 1-based line
/// number. Never enumerates files, reads disk, or writes output.
/// </summary>
public sealed record MbriCsvTable(string Source, string[] Headers, IReadOnlyList<MbriCsvRow> Rows)
{
    public int IndexOf(string header) => Array.IndexOf(Headers, header);

    public bool HasColumn(string header) => IndexOf(header) >= 0;

    /// <summary>Parses a required finite double; empty values throw (missing data must be explicit).</summary>
    public double Number(int row, int column)
    {
        var raw = Rows[row].Fields[column].Trim();
        if (!double.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value)
            || !double.IsFinite(value))
        {
            throw new MbriCsvException(Source, Rows[row].Line,
                $"列 '{Headers[column]}' 值 '{raw}' 不是有限数值");
        }
        return value;
    }

    /// <summary>Parses an optional finite double; an empty cell yields null.</summary>
    public double? OptionalNumber(int row, int column)
    {
        var raw = Rows[row].Fields[column].Trim();
        if (raw.Length == 0)
        {
            return null;
        }
        if (!double.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value)
            || !double.IsFinite(value))
        {
            throw new MbriCsvException(Source, Rows[row].Line,
                $"列 '{Headers[column]}' 值 '{raw}' 不是有限数值");
        }
        return value;
    }

    public string Text(int row, int column)
        => Rows[row].Fields[column].Trim();

    public static MbriCsvTable Parse(string source, string text)
    {
        if (text.Length > 0 && text[0] == '\uFEFF')
        {
            text = text[1..];
        }
        var rows = new List<MbriCsvRow>();
        string[]? headers = null;
        foreach (var (fields, line) in ReadRecords(source, text))
        {
            if (headers is null)
            {
                if (fields.Length == 0 || string.IsNullOrWhiteSpace(fields[0]))
                {
                    throw new MbriCsvException(source, line, "缺少表头(空首行)");
                }
                headers = fields;
                continue;
            }
            if (fields.Length != headers.Length)
            {
                throw new MbriCsvException(source, line,
                    $"列数 {fields.Length} 与表头 {headers.Length} 不一致");
            }
            if (fields.All(f => f.Length == 0))
            {
                continue; // skip fully blank lines
            }
            rows.Add(new MbriCsvRow(line, fields));
        }
        if (headers is null)
        {
            throw new MbriCsvException(source, 1, "文件为空, 无表头");
        }
        return new MbriCsvTable(source, headers, rows);
    }

    /// <summary>Yields each record's fields with its 1-based line number (RFC 4180 quotes).</summary>
    private static IEnumerable<(string[] Fields, int Line)> ReadRecords(string source, string text)
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
                    if (c == '\n')
                    {
                        line++;
                    }
                    field.Append(c);
                }
                continue;
            }
            switch (c)
            {
                case ',':
                    fields.Add(field.ToString());
                    field.Clear();
                    atFieldStart = true;
                    break;
                case '\r':
                    break;
                case '\n':
                    fields.Add(field.ToString());
                    field.Clear();
                    yield return (fields.ToArray(), line);
                    fields.Clear();
                    line++;
                    atFieldStart = true;
                    break;
                default:
                    field.Append(c);
                    atFieldStart = false;
                    break;
            }
        }
        if (quoted)
        {
            throw new MbriCsvException(source, line, "引号未闭合");
        }
        if (field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString());
            yield return (fields.ToArray(), line);
        }
    }
}

/// <summary>One CSV data row with its 1-based line number (header = line 1).</summary>
public sealed record MbriCsvRow(int Line, string[] Fields);
