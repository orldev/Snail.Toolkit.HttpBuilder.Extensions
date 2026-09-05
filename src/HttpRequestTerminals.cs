using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Snail.Toolkit.HttpBuilder.Extensions;

/// <summary>
/// The typed and streaming terminals of <see cref="IHttpRequestBuilder"/>.
/// </summary>
/// <remarks>
/// Terminals are extension methods over the frozen interface's
/// <see cref="IHttpRequestBuilder.SendCheckedAsync"/>, so a new response format is an
/// addition here, never a break for an implementor of the contract.
/// </remarks>
public static class HttpRequestTerminals
{
    /// <summary>
    /// The <c>data</c> terminator OpenAI ends a stream with, a convention most LLM
    /// providers copied. It is not part of the SSE specification.
    /// </summary>
    private const string DoneSentinel = "[DONE]";

    /// <summary>Sends the request and deserializes a successful JSON response.</summary>
    /// <returns>The body, or <c>default</c> when the response carries none.</returns>
    /// <exception cref="HttpBuilderException">The status was not a success.</exception>
    /// <remarks>
    /// Emptiness is judged by the bytes actually received, never by <c>Content-Length</c>,
    /// so a chunked response with no body reads as <c>default</c> rather than as a parse
    /// error.
    /// </remarks>
    public static async Task<TValue?> SendAsync<TValue>(
        this IHttpRequestBuilder builder, CancellationToken cancellationToken = default)
    {
        using var response = await builder
            .SendCheckedAsync(HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);

        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        if (body.Length == 0)
        {
            return default;
        }

        return JsonSerializer.Deserialize<TValue>(body, builder.JsonOptions);
    }

    /// <summary>Sends the request and returns a successful response as text.</summary>
    /// <exception cref="HttpBuilderException">The status was not a success.</exception>
    public static async Task<string> SendAsStringAsync(
        this IHttpRequestBuilder builder, CancellationToken cancellationToken = default)
    {
        using var response = await builder
            .SendCheckedAsync(HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);

        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends the request and streams a successful newline-delimited JSON response,
    /// yielding each line as it arrives rather than waiting for the body to end.
    /// Blank lines and JSON <c>null</c>s are skipped.
    /// </summary>
    /// <exception cref="HttpBuilderException">The status was not a success.</exception>
    /// <remarks>
    /// Lines are read to null, never through <see cref="StreamReader.EndOfStream"/>: on a
    /// live network stream that property blocks the thread with a synchronous read while
    /// it waits for the next chunk.
    /// </remarks>
    public static async IAsyncEnumerable<TValue> SendAsNdjsonAsync<TValue>(
        this IHttpRequestBuilder builder,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var response = await builder
            .SendCheckedAsync(HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var value = JsonSerializer.Deserialize<TValue>(line, builder.JsonOptions);

            if (value is not null)
            {
                yield return value;
            }
        }
    }

    /// <summary>
    /// Sends the request and streams the elements of a successful JSON-array response,
    /// yielding each element as it arrives rather than waiting for the closing bracket.
    /// JSON <c>null</c> elements are skipped.
    /// </summary>
    /// <exception cref="HttpBuilderException">The status was not a success.</exception>
    public static async IAsyncEnumerable<TValue> SendAsJsonStreamAsync<TValue>(
        this IHttpRequestBuilder builder,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var response = await builder
            .SendCheckedAsync(HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);

        await foreach (var value in JsonSerializer
                           .DeserializeAsyncEnumerable<TValue>(stream, builder.JsonOptions, cancellationToken)
                           .ConfigureAwait(false))
        {
            if (value is not null)
            {
                yield return value;
            }
        }
    }

    /// <summary>
    /// Sends the request and streams a successful <c>text/event-stream</c> response,
    /// yielding the parsed <c>data</c> payload of each server-sent event as it arrives.
    /// Comment lines and other fields are skipped, multi-line data is joined with
    /// newlines, JSON <c>null</c>s are skipped, and an OpenAI-style <c>[DONE]</c>
    /// sentinel ends the stream.
    /// </summary>
    /// <exception cref="HttpBuilderException">The status was not a success.</exception>
    public static async IAsyncEnumerable<TValue> SendAsSseAsync<TValue>(
        this IHttpRequestBuilder builder,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var received in builder
                           .SendAsSseEventsAsync<TValue>(cancellationToken)
                           .ConfigureAwait(false))
        {
            yield return received.Data;
        }
    }

    /// <summary>
    /// Sends the request and streams a successful <c>text/event-stream</c> response as
    /// whole events, keeping each event's name and the stream's last id alongside the
    /// parsed <c>data</c> payload. JSON <c>null</c>s are skipped, and an OpenAI-style
    /// <c>[DONE]</c> sentinel ends the stream.
    /// </summary>
    /// <exception cref="HttpBuilderException">The status was not a success.</exception>
    public static async IAsyncEnumerable<SseEvent<TValue>> SendAsSseEventsAsync<TValue>(
        this IHttpRequestBuilder builder,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var response = await builder
            .SendCheckedAsync(HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        await foreach (var received in SseReader.ReadAsync(reader, cancellationToken).ConfigureAwait(false))
        {
            if (received.Data == DoneSentinel)
            {
                yield break;
            }

            var value = JsonSerializer.Deserialize<TValue>(received.Data, builder.JsonOptions);

            if (value is not null)
            {
                yield return new SseEvent<TValue>(received.Event, received.Id, value);
            }
        }
    }
}
