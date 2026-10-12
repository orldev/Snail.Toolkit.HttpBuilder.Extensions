using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Snail.Toolkit.HttpBuilder.Extensions;

/// <summary>
/// The typed and streaming terminals of <see cref="IHttpRequestBuilder"/>.
/// </summary>
/// <remarks>
/// Terminals are extension methods over the frozen interface's
/// <see cref="IHttpRequestBuilder.SendCheckedAsync"/>, so a new response format is an
/// addition here, never a break for an implementor of the contract. Each JSON terminal
/// comes twice: reading with <see cref="IHttpRequestBuilder.JsonOptions"/>, which resolves
/// types by reflection, and reading with a <see cref="JsonTypeInfo{T}"/> from a
/// <c>JsonSerializerContext</c>, which works trimmed and under Native AOT.
/// </remarks>
public static class HttpRequestTerminals
{
    /// <summary>
    /// The <c>data</c> terminator OpenAI ends a stream with, a convention most LLM
    /// providers copied. It is not part of the SSE specification.
    /// </summary>
    private const string DoneSentinel = "[DONE]";

    /// <summary>Why the terminals that read with <see cref="IHttpRequestBuilder.JsonOptions"/> are not native.</summary>
    internal const string ReflectionMessage =
        "Reads JSON with IHttpRequestBuilder.JsonOptions, which resolves types by reflection. "
        + "Trimmed and Native AOT apps pass a JsonTypeInfo<T> from a JsonSerializerContext instead.";

    /// <summary>Why each overload taking a <see cref="JsonTypeInfo{T}"/> may keep an optional token.</summary>
    private const string BesideTheReflectionOverload =
        "The overload is told apart by its required JsonTypeInfo<T>, so no call binds differently: the shape "
        + "System.Net.Http.Json gives GetFromJsonAsync. A required token here would be the one call that cannot omit it.";

    /// <summary>Sends the request and deserializes a successful JSON response.</summary>
    /// <returns>The body, or <c>default</c> when the response carries none.</returns>
    /// <exception cref="HttpBuilderException">The status was not a success.</exception>
    /// <remarks>
    /// Emptiness is judged by the bytes actually received, never by <c>Content-Length</c>,
    /// so a chunked response with no body reads as <c>default</c> rather than as a parse
    /// error. The body is parsed from a pooled copy of the buffered response, not from a
    /// new array of its size.
    /// </remarks>
    [RequiresUnreferencedCode(ReflectionMessage)]
    [RequiresDynamicCode(ReflectionMessage)]
    public static async Task<TValue?> SendAsync<TValue>(
        this IHttpRequestBuilder builder, CancellationToken cancellationToken = default)
    {
        using var response = await builder
            .SendCheckedAsync(HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);

        using var body = await PooledBody.ReadAsync(response.Content, cancellationToken).ConfigureAwait(false);

        if (body.Length == 0)
        {
            return default;
        }

        return JsonSerializer.Deserialize<TValue>(body.Span, builder.JsonOptions);
    }

    /// <summary>
    /// Sends the request and deserializes a successful JSON response with the
    /// source-generated metadata <c>typeInfo</c> carries, so no reflection is involved.
    /// </summary>
    /// <returns>The body, or <c>default</c> when the response carries none.</returns>
    /// <exception cref="HttpBuilderException">The status was not a success.</exception>
    /// <remarks>Reads as <see cref="SendAsync{TValue}(IHttpRequestBuilder, CancellationToken)"/> does.</remarks>
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = BesideTheReflectionOverload)]
    public static async Task<TValue?> SendAsync<TValue>(
        this IHttpRequestBuilder builder, JsonTypeInfo<TValue> typeInfo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);

        using var response = await builder
            .SendCheckedAsync(HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);

        using var body = await PooledBody.ReadAsync(response.Content, cancellationToken).ConfigureAwait(false);

        if (body.Length == 0)
        {
            return default;
        }

        return JsonSerializer.Deserialize(body.Span, typeInfo);
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
    /// Lines are split on their UTF-8 bytes and each is parsed from them, without a string;
    /// a line ends at <c>\r</c>, <c>\n</c> or both, as a text reader ends it.
    /// </remarks>
    [RequiresUnreferencedCode(ReflectionMessage)]
    [RequiresDynamicCode(ReflectionMessage)]
    public static IAsyncEnumerable<TValue> SendAsNdjsonAsync<TValue>(
        this IHttpRequestBuilder builder, CancellationToken cancellationToken = default) =>
        ReadNdjsonAsync(
            builder,
            builder,
            static (line, reading) => JsonSerializer.Deserialize<TValue>(line, reading.JsonOptions),
            static (line, reading) => JsonSerializer.Deserialize<TValue>(line, reading.JsonOptions),
            cancellationToken);

    /// <summary>
    /// Sends the request and streams a successful newline-delimited JSON response, each
    /// line read with the source-generated metadata <c>typeInfo</c> carries.
    /// </summary>
    /// <exception cref="HttpBuilderException">The status was not a success.</exception>
    /// <remarks>Reads as <see cref="SendAsNdjsonAsync{TValue}(IHttpRequestBuilder, CancellationToken)"/> does.</remarks>
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = BesideTheReflectionOverload)]
    public static IAsyncEnumerable<TValue> SendAsNdjsonAsync<TValue>(
        this IHttpRequestBuilder builder, JsonTypeInfo<TValue> typeInfo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);

        return ReadNdjsonAsync(
            builder,
            typeInfo,
            static (line, metadata) => JsonSerializer.Deserialize(line, metadata),
            static (line, metadata) => JsonSerializer.Deserialize(line, metadata),
            cancellationToken);
    }

    private static async IAsyncEnumerable<TValue> ReadNdjsonAsync<TValue, TShape>(
        IHttpRequestBuilder builder,
        TShape shape,
        Func<ReadOnlySpan<byte>, TShape, TValue?> parseUtf8,
        Func<string, TShape, TValue?> parseText,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var response = await builder
            .SendCheckedAsync(HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);

        await foreach (var value in NdjsonLines
                           .ReadAsync(stream, shape, parseUtf8, parseText, cancellationToken)
                           .ConfigureAwait(false))
        {
            yield return value;
        }
    }

    /// <summary>
    /// Sends the request and streams the elements of a successful JSON-array response,
    /// yielding each element as it arrives rather than waiting for the closing bracket.
    /// JSON <c>null</c> elements are skipped.
    /// </summary>
    /// <exception cref="HttpBuilderException">The status was not a success.</exception>
    [RequiresUnreferencedCode(ReflectionMessage)]
    [RequiresDynamicCode(ReflectionMessage)]
    public static IAsyncEnumerable<TValue> SendAsJsonStreamAsync<TValue>(
        this IHttpRequestBuilder builder, CancellationToken cancellationToken = default) =>
        ReadJsonStreamAsync(
            builder,
            builder,
            static (stream, reading, token) =>
                JsonSerializer.DeserializeAsyncEnumerable<TValue>(stream, reading.JsonOptions, token),
            cancellationToken);

    /// <summary>
    /// Sends the request and streams the elements of a successful JSON-array response,
    /// each read with the source-generated metadata <c>typeInfo</c> carries.
    /// </summary>
    /// <exception cref="HttpBuilderException">The status was not a success.</exception>
    /// <remarks>Reads as <see cref="SendAsJsonStreamAsync{TValue}(IHttpRequestBuilder, CancellationToken)"/> does.</remarks>
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = BesideTheReflectionOverload)]
    public static IAsyncEnumerable<TValue> SendAsJsonStreamAsync<TValue>(
        this IHttpRequestBuilder builder, JsonTypeInfo<TValue> typeInfo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);

        return ReadJsonStreamAsync(
            builder,
            typeInfo,
            static (stream, metadata, token) => JsonSerializer.DeserializeAsyncEnumerable(stream, metadata, token),
            cancellationToken);
    }

    private static async IAsyncEnumerable<TValue> ReadJsonStreamAsync<TValue, TShape>(
        IHttpRequestBuilder builder,
        TShape shape,
        Func<Stream, TShape, CancellationToken, IAsyncEnumerable<TValue?>> parse,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var response = await builder
            .SendCheckedAsync(HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);

        await foreach (var value in parse(stream, shape, cancellationToken).ConfigureAwait(false))
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
    [RequiresUnreferencedCode(ReflectionMessage)]
    [RequiresDynamicCode(ReflectionMessage)]
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
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = BesideTheReflectionOverload)]
    public static IAsyncEnumerable<TValue> SendAsSseAsync<TValue>(
        this IHttpRequestBuilder builder, JsonTypeInfo<TValue> typeInfo, CancellationToken cancellationToken = default)
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
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = BesideTheReflectionOverload)]
    public static IAsyncEnumerable<SseEvent<TValue>> SendAsSseEventsAsync<TValue>(
        this IHttpRequestBuilder builder, JsonTypeInfo<TValue> typeInfo, CancellationToken cancellationToken = default)
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
