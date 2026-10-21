using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Snail.Toolkit.HttpBuilder.Extensions;

public static partial class HttpRequestTerminals
{
    /// <summary>
    /// The <c>data</c> terminator OpenAI ends a stream with, a convention most LLM
    /// providers copied. It is not part of the SSE specification.
    /// </summary>
    private const string DoneSentinel = "[DONE]";

    /// <summary>
    /// Sends the request and streams a successful <c>text/event-stream</c> response,
    /// yielding the parsed <c>data</c> payload of each server-sent event as it arrives.
    /// Comment lines and other fields are skipped, multi-line data is joined with
    /// newlines, JSON <c>null</c>s are skipped, and an OpenAI-style <c>[DONE]</c>
    /// sentinel ends the stream.
    /// </summary>
    /// <exception cref="HttpBuilderException">The status was not a success.</exception>
    [RequiresUnreferencedCode(ReflectionMessage)]
    [RequiresDynamicCode(ReflectionMessage)]
    [SuppressMessage("ApiDesign", "RS0027:API with optional parameter(s) should have the most parameters amongst its public overloads", Justification = ReleasedSignature)]
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
    /// Sends the request and streams a successful <c>text/event-stream</c> response,
    /// yielding the <c>data</c> payload of each server-sent event read with the
    /// source-generated metadata <c>typeInfo</c> carries.
    /// </summary>
    /// <exception cref="HttpBuilderException">The status was not a success.</exception>
    /// <remarks>Reads as <see cref="SendAsSseAsync{TValue}(IHttpRequestBuilder, CancellationToken)"/> does.</remarks>
    public static IAsyncEnumerable<TValue> SendAsSseAsync<TValue>(
        this IHttpRequestBuilder builder, JsonTypeInfo<TValue> typeInfo, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);

        return ReadSseDataAsync(builder.SendAsSseEventsAsync(typeInfo, cancellationToken), cancellationToken);
    }

    private static async IAsyncEnumerable<TValue> ReadSseDataAsync<TValue>(
        IAsyncEnumerable<SseEvent<TValue>> events,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var received in events.WithCancellation(cancellationToken).ConfigureAwait(false))
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
    [RequiresUnreferencedCode(ReflectionMessage)]
    [RequiresDynamicCode(ReflectionMessage)]
    [SuppressMessage("ApiDesign", "RS0027:API with optional parameter(s) should have the most parameters amongst its public overloads", Justification = ReleasedSignature)]
    public static IAsyncEnumerable<SseEvent<TValue>> SendAsSseEventsAsync<TValue>(
        this IHttpRequestBuilder builder, CancellationToken cancellationToken = default) =>
        ReadSseEventsAsync(
            builder,
            builder,
            static (data, reading) => JsonSerializer.Deserialize<TValue>(data, reading.JsonOptions),
            cancellationToken);

    /// <summary>
    /// Sends the request and streams a successful <c>text/event-stream</c> response as
    /// whole events, each <c>data</c> payload read with the source-generated metadata
    /// <c>typeInfo</c> carries.
    /// </summary>
    /// <exception cref="HttpBuilderException">The status was not a success.</exception>
    /// <remarks>Reads as <see cref="SendAsSseEventsAsync{TValue}(IHttpRequestBuilder, CancellationToken)"/> does.</remarks>
    public static IAsyncEnumerable<SseEvent<TValue>> SendAsSseEventsAsync<TValue>(
        this IHttpRequestBuilder builder, JsonTypeInfo<TValue> typeInfo, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);

        return ReadSseEventsAsync(
            builder, typeInfo, static (data, metadata) => JsonSerializer.Deserialize(data, metadata), cancellationToken);
    }

    private static async IAsyncEnumerable<SseEvent<TValue>> ReadSseEventsAsync<TValue, TShape>(
        IHttpRequestBuilder builder,
        TShape shape,
        Func<string, TShape, TValue?> parse,
        [EnumeratorCancellation] CancellationToken cancellationToken)
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

            var value = parse(received.Data, shape);

            if (value is not null)
            {
                yield return new SseEvent<TValue>(received.Event, received.Id, value);
            }
        }
    }
}
