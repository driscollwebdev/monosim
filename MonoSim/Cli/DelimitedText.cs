using System.Text;

namespace MonoSim.Cli;

public static class DelimitedText
{
    public static string Format(IEnumerable<string> fields, char delimiter) => string.Join(delimiter, fields.Select(field =>
    {
        if (field.Contains(delimiter) || field.Contains('"') || field.Contains('\r') || field.Contains('\n'))
        {
            return "\"" + field.Replace("\"", "\"\"") + "\"";
        }
        return field;
    }));

    public static IEnumerable<string[]> Read(TextReader reader, char delimiter)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var closed = false;
        var started = false;
        while (true)
        {
            var next = reader.Read();
            if (next == -1)
            {
                if (quoted)
                {
                    throw new InvalidDataException("Results file contains an unfinished quoted field.");
                }
                if (started || fields.Count > 0)
                {
                    fields.Add(field.ToString());
                    yield return fields.ToArray();
                }
                yield break;
            }
            var c = (char)next;
            if (quoted)
            {
                if (c == '"')
                {
                    if (reader.Peek() == '"')
                    {
                        reader.Read();
                        field.Append('"');
                    }
                    else
                    {
                        quoted = false;
                        closed = true;
                    }
                }
                else
                {
                    field.Append(c);
                }
                continue;
            }
            if (closed && c != delimiter && c is not ('\r' or '\n'))
            {
                throw new InvalidDataException("Unexpected text after a quoted results field.");
            }
            if (c == delimiter)
            {
                fields.Add(field.ToString());
                field.Clear();
                started = false;
                closed = false;
            }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && reader.Peek() == '\n')
                {
                    reader.Read();
                }
                fields.Add(field.ToString());
                yield return fields.ToArray();
                fields.Clear();
                field.Clear();
                started = false;
                closed = false;
            }
            else if (c == '"')
            {
                if (started)
                {
                    throw new InvalidDataException("Unexpected quote in an unquoted results field.");
                }
                quoted = true;
                started = true;
            }
            else
            {
                field.Append(c);
                started = true;
            }
        }
    }
}
