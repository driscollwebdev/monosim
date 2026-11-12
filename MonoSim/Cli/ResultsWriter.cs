using System.Text;

namespace MonoSim.Cli;

/// <summary>Serializes completed rows under one lock; validates existing files before appending.</summary>
public sealed class ResultsWriter : IDisposable
{
    private readonly StreamWriter writer;
    private readonly char delimiter;
    private readonly object gate = new();
    public string FilePath { get; }

    private ResultsWriter(FileStream stream, string path, char delimiter, bool header, bool separator)
    {
        FilePath = path;
        this.delimiter = delimiter;
        writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\r\n" };
        if (separator)
        {
            writer.WriteLine();
        }
        if (header)
        {
            writer.WriteLine(DelimitedText.Format(SimulationOutcome.Headers, delimiter));
        }
        writer.Flush();
    }

    public static ResultsWriter Open(string path, char delimiter, TextReader input, TextWriter prompt, bool interactive)
    {
        while (true)
        {
            path = Path.GetFullPath(path);
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                FileStream created;
                try
                {
                    created = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                }
                catch (IOException) when (File.Exists(path))
                {
                    // Another process created it after our existence check; require an explicit choice.
                    continue;
                }
                return Construct(created, path, delimiter, header: true, separator: false);
            }
            if (!interactive)
            {
                throw new IOException($"Results file already exists: {path}. Run interactively to append, overwrite, or choose another filename.");
            }
            prompt.Write($"Results file exists: {path}\n[A]ppend, [O]verwrite, [N]ew filename, or [Q]uit: ");
            prompt.Flush();
            var choice = input.ReadLine()?.Trim().ToLowerInvariant();
            if (choice is null or "q" or "quit")
            {
                throw new OperationCanceledException("Results-file selection cancelled.");
            }
            if (choice is "n" or "new")
            {
                prompt.Write("New results path: ");
                prompt.Flush();
                var next = input.ReadLine();
                if (next is null)
                {
                    throw new OperationCanceledException("Results-file selection cancelled.");
                }
                if (!string.IsNullOrWhiteSpace(next))
                {
                    path = next.Trim();
                }
                continue;
            }
            if (choice is "o" or "overwrite")
            {
                return Construct(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read), path, delimiter, header: true, separator: false);
            }
            if (choice is "a" or "append")
            {
                var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
                try
                {
                    if (stream.Length == 0)
                    {
                        return Construct(stream, path, delimiter, header: true, separator: false);
                    }
                    using (var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true, leaveOpen: true))
                    {
                        using var rows = DelimitedText.Read(reader, delimiter).GetEnumerator();
                        if (!rows.MoveNext() || !rows.Current.SequenceEqual(SimulationOutcome.Headers))
                        {
                            throw new InvalidDataException("Existing results header or delimiter does not match this output format.");
                        }
                        if (reader.CurrentEncoding.CodePage != Encoding.UTF8.CodePage)
                        {
                            throw new InvalidDataException("Existing results must use UTF-8 encoding.");
                        }
                        while (rows.MoveNext())
                        {
                            if (rows.Current.Length != SimulationOutcome.Headers.Count)
                            {
                                throw new InvalidDataException("Existing results contain a malformed row.");
                            }
                        }
                    }
                    stream.Seek(-1, SeekOrigin.End);
                    var last = stream.ReadByte();
                    stream.Seek(0, SeekOrigin.End);
                    return Construct(stream, path, delimiter, header: false, separator: last is not (10 or 13));
                }
                catch
                {
                    stream.Dispose();
                    throw;
                }
            }
            prompt.WriteLine("Choose append, overwrite, new filename, or quit.");
        }
    }

    private static ResultsWriter Construct(FileStream stream, string path, char delimiter, bool header, bool separator)
    {
        try
        {
            return new(stream, path, delimiter, header, separator);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public void Write(SimulationOutcome outcome)
    {
        var row = DelimitedText.Format(outcome.Fields(), delimiter);
        lock (gate)
        {
            writer.WriteLine(row);
            writer.Flush();
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            writer.Dispose();
        }
    }
}
