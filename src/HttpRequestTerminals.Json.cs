using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Snail.Toolkit.HttpBuilder.Extensions;

public static partial class HttpRequestTerminals
{
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
    [SuppressMessage("ApiDesign", "RS0027:API with optional parameter(s) should have the most parameters amongst its public overloads", Justification = ReleasedSignature)]
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
    public static async Task<TValue?> SendAsync<TValue>(
        this IHttpRequestBuilder builder, JsonTypeInfo<TValue> typeInfo, CancellationToken cancellationToken)
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

    /// <summary>
    /// Sends the request and deserializes a successful JSON response as it arrives, without
    /// holding the whole body first: for large bodies.
    /// </summary>
    /// <returns>The body, or <c>default</c> when the response carries none.</returns>
    /// <exception cref="HttpBuilderException">The status was not a success.</exception>
    /// <exception cref="TaskCanceledException">
    /// The client's <see cref="HttpClient.Timeout"/> elapsed before the body was read, or the
    /// token was cancelled.
    /// </exception>
    /// <exception cref="HttpRequestException">The connection failed while the body was read.</exception>
    /// <remarks>
    /// The client's timeout covers the whole request, the body included, as it does for
    /// <see cref="SendAsync{TValue}(IHttpRequestBuilder, CancellationToken)"/>, and a timeout, a
    /// cancelled token or a broken connection surface as the same exceptions. Emptiness is
    /// judged by the bytes received. A body is parsed as it arrives, so an answer of a few
    /// kilobytes is read faster buffered.
    /// </remarks>
    [RequiresUnreferencedCode(ReflectionMessage)]
    [RequiresDynamicCode(ReflectionMessage)]
    public static Task<TValue?> SendStreamedAsync<TValue>(
        this IHttpRequestBuilder builder, CancellationToken cancellationToken) =>
        StreamedBody.ReadAsync(
            builder,
            builder,
            static (body, reading, token) => JsonSerializer.DeserializeAsync<TValue>(body, reading.JsonOptions, token),
            cancellationToken);

    /// <summary>
    /// Sends the request and deserializes a successful JSON response as it arrives, with the
    /// source-generated metadata <c>typeInfo</c> carries.
    /// </summary>
    /// <returns>The body, or <c>default</c> when the response carries none.</returns>
    /// <exception cref="HttpBuilderException">The status was not a success.</exception>
    /// <exception cref="TaskCanceledException">
    /// The client's <see cref="HttpClient.Timeout"/> elapsed before the body was read, or the
    /// token was cancelled.
    /// </exception>
    /// <exception cref="HttpRequestException">The connection failed while the body was read.</exception>
    /// <remarks>Reads as <see cref="SendStreamedAsync{TValue}(IHttpRequestBuilder, CancellationToken)"/> does.</remarks>
    public static Task<TValue?> SendStreamedAsync<TValue>(
        this IHttpRequestBuilder builder, JsonTypeInfo<TValue> typeInfo, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);

        return StreamedBody.ReadAsync(
            builder,
            typeInfo,
            static (body, metadata, token) => JsonSerializer.DeserializeAsync(body, metadata, token),
            cancellationToken);
    }
}
