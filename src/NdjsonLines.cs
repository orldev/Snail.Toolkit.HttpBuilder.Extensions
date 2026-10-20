using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Unicode;

namespace Snail.Toolkit.HttpBuilder.Extensions;

/// <summary>
/// Reads newline-delimited JSON as UTF-8 lines, each parsed from its bytes: no string per line and no
/// transcoding to UTF-16 and back.
/// </summary>
/// <remarks>
/// <para>
/// The values are those a <see cref="StreamReader"/> reading lines gave: a line ends at <c>\r</c>,
/// <c>\n</c> or both, a blank or whitespace line is skipped, a UTF-8 byte order mark at the start is
/// dropped, and a line that is not valid UTF-8 is read as the reader decoded it, with replacement
/// characters. A body that starts like a UTF-16 or UTF-32 byte order mark is read by a reader, as before.
/// </para>
/// <para>
/// Each value is yielded as soon as its line has arrived, never after the next one.
/// </para>
/// </remarks>
internal static class NdjsonLines
{
    private static readonly SearchValues<byte> Blank = SearchValues.Create(BlankBytes());

    /// <summary>Yields the value of each line that is neither blank nor JSON <c>null</c>.</summary>
    public static async IAsyncEnumerable<TValue> ReadAsync<TValue, TShape>(
        Stream stream,
        TShape shape,
        Func<ReadOnlySpan<byte>, TShape, TValue?> parseUtf8,
        Func<string, TShape, TValue?> parseText,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var reader = PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: true));

        try
        {
            if (!await TrySkipOrderMarkAsync(reader, cancellationToken).ConfigureAwait(false))
            {
                await foreach (var value in ReadByReaderAsync(reader.AsStream(leaveOpen: true), shape, parseText, cancellationToken)
                                   .ConfigureAwait(false))
                {
                    yield return value;
                }

                yield break;
            }

            var searched = 0L;

            while (true)
            {
                var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                var buffer = result.Buffer;

                while (TryTakeLine(ref buffer, ref searched, result.IsCompleted, out var line))
                {
                    if (TryParse(line, shape, parseUtf8, parseText, out var value))
                    {
                        yield return value;
                    }
                }

                reader.AdvanceTo(buffer.Start, buffer.End);

                if (result.IsCompleted)
                {
                    break;
                }
            }
        }
        finally
        {
            await reader.CompleteAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Drops a UTF-8 byte order mark. Answers false when the body starts with a byte that may begin another
    /// encoding's mark, or only part of UTF-8's, which a reader is left to judge as before.
    /// </summary>
    private static async ValueTask<bool> TrySkipOrderMarkAsync(PipeReader reader, CancellationToken cancellationToken)
    {
        while (true)
        {
            var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = result.Buffer;

            if (buffer.IsEmpty)
            {
                reader.AdvanceTo(buffer.Start);

                return true;
            }

            var first = buffer.FirstSpan[0];

            if (first is not (0xEF or 0xFE or 0xFF or 0x00))
            {
                reader.AdvanceTo(buffer.Start);

                return true;
            }

            if (first == 0xEF && buffer.Length >= 3)
            {
                var isMark = IsUtf8OrderMark(buffer.Slice(0, 3));
                reader.AdvanceTo(isMark ? buffer.GetPosition(3) : buffer.Start);

                return isMark;
            }

            if (first != 0xEF || result.IsCompleted)
            {
                reader.AdvanceTo(buffer.Start);

                return false;
            }

            reader.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    private static bool IsUtf8OrderMark(ReadOnlySequence<byte> start)
    {
        Span<byte> bytes = stackalloc byte[3];
        start.CopyTo(bytes);

        return bytes.SequenceEqual(Encoding.UTF8.Preamble);
    }

    /// <summary>Takes the next line off the buffer: up to a line end, or the rest once the body has ended.</summary>
    /// <remarks>
    /// The pipe hands back what was not consumed together with what arrived, so <paramref name="searched"/> keeps
    /// how much of the buffer is already known to hold no line end, and only the rest is searched. Searched from the
    /// start on every read, one line cost the square of its length: 16 MB in 4 KB reads took 1.8–2.4 s, 77–79 ms
    /// searched once.
    /// </remarks>
    private static bool TryTakeLine(
        ref ReadOnlySequence<byte> buffer, ref long searched, bool isCompleted, out ReadOnlySequence<byte> line)
    {
        var lines = new SequenceReader<byte>(buffer.Slice(searched));

        if (lines.TryAdvanceToAny("\r\n"u8, advancePastDelimiter: false))
        {
            line = buffer.Slice(buffer.Start, lines.Position);
            buffer = buffer.Slice(lines.Position).Slice(1);
            searched = 0;

            return true;
        }

        if (isCompleted && !buffer.IsEmpty)
        {
            line = buffer;
            buffer = buffer.Slice(buffer.End);
            searched = 0;

            return true;
        }

        searched = buffer.Length;
        line = default;

        return false;
    }

    private static bool TryParse<TValue, TShape>(
        ReadOnlySequence<byte> line,
        TShape shape,
        Func<ReadOnlySpan<byte>, TShape, TValue?> parseUtf8,
        Func<string, TShape, TValue?> parseText,
        out TValue value)
    {
        if (line.IsSingleSegment)
        {
            return TryParse(line.FirstSpan, shape, parseUtf8, parseText, out value);
        }

        var length = checked((int)line.Length);
        var copy = ArrayPool<byte>.Shared.Rent(length);

        try
        {
            line.CopyTo(copy);

            return TryParse(copy.AsSpan(0, length), shape, parseUtf8, parseText, out value);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(copy);
        }
    }

    private static bool TryParse<TValue, TShape>(
        ReadOnlySpan<byte> line,
        TShape shape,
        Func<ReadOnlySpan<byte>, TShape, TValue?> parseUtf8,
        Func<string, TShape, TValue?> parseText,
        out TValue value)
    {
        value = default!;

        if (IsBlank(line))
        {
            return false;
        }

        var parsed = Utf8.IsValid(line) ? parseUtf8(line, shape) : parseText(Encoding.UTF8.GetString(line), shape);

        if (parsed is null)
        {
            return false;
        }

        value = parsed;

        return true;
    }

    /// <summary>Whether the line holds only white space, as <see cref="string.IsNullOrWhiteSpace"/> judges it.</summary>
    /// <remarks>
    /// A byte outside <see cref="Blank"/> is ASCII and not white space, so the line has content; a line of
    /// white space and characters beyond ASCII is decoded to be judged.
    /// </remarks>
    private static bool IsBlank(ReadOnlySpan<byte> line)
    {
        if (line.IndexOfAnyExcept(Blank) >= 0)
        {
            return false;
        }

        return Ascii.IsValid(line) || string.IsNullOrWhiteSpace(Encoding.UTF8.GetString(line));
    }

    /// <summary>ASCII white space as <see cref="char.IsWhiteSpace(char)"/> has it, and every byte beyond ASCII.</summary>
    private static byte[] BlankBytes()
    {
        var bytes = new List<byte>();

        for (var code = 0; code <= byte.MaxValue; code++)
        {
            if (code > 0x7F || char.IsWhiteSpace((char)code))
            {
                bytes.Add((byte)code);
            }
        }

        return [.. bytes];
    }

    /// <summary>The lines read as they were before: by a reader, each line a string.</summary>
    private static async IAsyncEnumerable<TValue> ReadByReaderAsync<TValue, TShape>(
        Stream stream,
        TShape shape,
        Func<string, TShape, TValue?> parseText,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var value = parseText(line, shape);

            if (value is not null)
            {
                yield return value;
            }
        }
    }
}
