namespace Aurora.Adapters.Plugins;

/// <summary>What a read of one plugin protocol line produced.</summary>
public enum LineStatus
{
    /// <summary>A complete line within the size limit; <see cref="LineResult.Line"/> holds it.</summary>
    Line,

    /// <summary>The stream ended with no more lines.</summary>
    End,

    /// <summary>
    /// The line exceeded the limit and was discarded. The reader has resynchronised to the start of
    /// the next line, so reading can continue; nothing oversized was retained.
    /// </summary>
    OverLimit,
}

/// <summary>The outcome of <see cref="BoundedLineReader.ReadLineAsync"/>.</summary>
public readonly record struct LineResult(LineStatus Status, string? Line)
{
    internal static readonly LineResult End = new(LineStatus.End, null);
    internal static readonly LineResult TooLong = new(LineStatus.OverLimit, null);

    internal static LineResult Of(string line) => new(LineStatus.Line, line);
}

/// <summary>
/// Reads newline-delimited text a line at a time, refusing any line past a fixed length.
/// </summary>
/// <remarks>
/// <see cref="StreamReader.ReadLineAsync(System.Threading.CancellationToken)"/> has no size bound:
/// a producer that never emits a newline makes it buffer without limit. That is the whole of the
/// F-4 exposure — a service plugin's stdout is untrusted and a single enormous line would make the
/// host allocate all of it before anything could reject it (docs/adr/0079).
/// <para>
/// This reads through a small fixed scratch buffer and accumulates at most <c>maxChars</c>
/// characters. When a line would exceed that, the accumulator is released and the rest of the line
/// is read and discarded until the next newline, so memory stays bounded and the stream stays
/// framed — the caller is told the frame was dropped and carries on with the next one. The bytes of
/// an oversized line are still read off the pipe (they have to be, to resynchronise), but they are
/// never held.
/// </para>
/// </remarks>
public sealed class BoundedLineReader
{
    private readonly TextReader _reader;
    private readonly int _maxChars;
    private readonly char[] _scratch = new char[8192];
    private int _length;
    private int _position;

    public BoundedLineReader(TextReader reader, int maxChars)
    {
        _reader = reader;
        _maxChars = maxChars;
    }

    public async Task<LineResult> ReadLineAsync(CancellationToken ct)
    {
        var builder = new System.Text.StringBuilder();
        var overflowed = false;
        var sawAnything = false;

        while (true)
        {
            if (_position >= _length)
            {
                _length = await _reader.ReadAsync(_scratch.AsMemory(), ct).ConfigureAwait(false);
                _position = 0;

                if (_length == 0)
                {
                    // End of stream. A trailing line with no newline is still a line; a line that
                    // had overflowed is still a refusal.
                    if (overflowed)
                    {
                        return LineResult.TooLong;
                    }

                    return sawAnything ? LineResult.Of(Trim(builder)) : LineResult.End;
                }
            }

            while (_position < _length)
            {
                var c = _scratch[_position++];
                sawAnything = true;

                if (c == '\n')
                {
                    return overflowed ? LineResult.TooLong : LineResult.Of(Trim(builder));
                }

                if (overflowed)
                {
                    // Discard until the newline; nothing is retained.
                    continue;
                }

                if (builder.Length >= _maxChars)
                {
                    // One character past the budget. Drop what was accumulated and switch to
                    // discarding the remainder of this line.
                    overflowed = true;
                    builder.Clear();
                    continue;
                }

                builder.Append(c);
            }
        }
    }

    /// <summary>Strips a single trailing carriage return, so CRLF and LF frame the same.</summary>
    private static string Trim(System.Text.StringBuilder builder)
    {
        if (builder.Length > 0 && builder[^1] == '\r')
        {
            builder.Length--;
        }

        return builder.ToString();
    }
}
