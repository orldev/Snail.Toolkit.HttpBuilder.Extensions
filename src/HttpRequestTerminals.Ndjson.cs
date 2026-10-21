using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Snail.Toolkit.HttpBuilder.Extensions;

public static partial class HttpRequestTerminals
{
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
    [SuppressMessage("ApiDesign", "RS0027:API with optional parameter(s) should have the most parameters amongst its public overloads", Justification = ReleasedSignature)]
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
    public static IAsyncEnumerable<TValue> SendAsNdjsonAsync<TValue>(
        this IHttpRequestBuilder builder, JsonTypeInfo<TValue> typeInfo, CancellationToken cancellationToken)
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
}
