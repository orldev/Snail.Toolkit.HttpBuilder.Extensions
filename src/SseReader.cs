using System.Runtime.CompilerServices;
using System.Text;

namespace Snail.Toolkit.HttpBuilder.Extensions;

/// <summary>One server-sent event as it came off the wire, its data still text.</summary>
internal sealed record ReceivedSseEvent(string? Event, string? Id, string Data);

/// <summary>
/// Reads the <c>text/event-stream</c> wire format: a field is named before the first
/// colon, one space after the colon belongs to the syntax, a line starting with a colon
/// is a comment, and a blank line separates events.
/// </summary>
internal static class SseReader
{
    /// <summary>Yields each event that carries data, joining multi-line data with newlines.</summary>
    /// <remarks>
    /// An event is dispatched on its blank separator line and, past the letter of the SSE
    /// specification, once more at end of stream, because servers routinely omit the
    /// final separator. Per the specification the <c>id</c> value outlives its event
    /// until the stream replaces it, while the event type resets after every dispatch.
    /// Lines are read to null, never through <see cref="StreamReader.EndOfStream"/>,
    /// which blocks the thread with a synchronous read on a live network stream.
    /// </remarks>
    public static async IAsyncEnumerable<ReceivedSseEvent> ReadAsync(
        StreamReader reader, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? eventType = null;
        string? id = null;
        var data = new StringBuilder();

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    yield return new ReceivedSseEvent(eventType, id, data.ToString());
                }

                eventType = null;
                data.Clear();
                continue;
            }

            if (line[0] == ':')
            {
                continue;
            }

            ReadField(line, out var name, out var value);

            switch (name)
            {
                case "data":
                    if (data.Length > 0)
                    {
                        data.Append('\n');
                    }

                    data.Append(value);
                    break;
                case "event":
                    eventType = value.ToString();
                    break;
                case "id":
                    id = value.ToString();
                    break;
            }
        }

        if (data.Length > 0)
        {
            yield return new ReceivedSseEvent(eventType, id, data.ToString());
        }
    }

    /// <summary>Splits an SSE field line into its name and value, both views of the line.</summary>
    /// <remarks>
    /// Views rather than substrings: a <c>data</c> value goes straight into the event's buffer, and
    /// only the fields kept past the line, <c>event</c> and <c>id</c>, become strings.
    /// </remarks>
    private static void ReadField(string line, out ReadOnlySpan<char> name, out ReadOnlySpan<char> value)
    {
        var colon = line.IndexOf(':', StringComparison.Ordinal);

        if (colon < 0)
        {
            name = line;
            value = [];

            return;
        }

        name = line.AsSpan(0, colon);
        value = line.AsSpan(colon + 1);

        if (value.StartsWith(' '))
        {
            value = value[1..];
        }
    }
}
