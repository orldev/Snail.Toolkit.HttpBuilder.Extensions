using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Snail.Toolkit.HttpBuilder.Extensions;

public static partial class HttpRequestTerminals
{
    /// <summary>
    /// Sends the request and streams the elements of a successful JSON-array response,
    /// yielding each element as it arrives rather than waiting for the closing bracket.
    /// JSON <c>null</c> elements are skipped.
    /// </summary>
    /// <exception cref="HttpBuilderException">The status was not a success.</exception>
    [RequiresUnreferencedCode(ReflectionMessage)]
    [RequiresDynamicCode(ReflectionMessage)]
    [SuppressMessage("ApiDesign", "RS0027:API with optional parameter(s) should have the most parameters amongst its public overloads", Justification = ReleasedSignature)]
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
    public static IAsyncEnumerable<TValue> SendAsJsonStreamAsync<TValue>(
        this IHttpRequestBuilder builder, JsonTypeInfo<TValue> typeInfo, CancellationToken cancellationToken)
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
}
