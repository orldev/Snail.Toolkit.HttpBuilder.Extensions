using System.Net.Http.Json;
using System.Text.Json.Serialization.Metadata;

namespace Snail.Toolkit.HttpBuilder.Extensions;

/// <summary>
/// Request bodies described by source-generated JSON metadata, beside the reflection-based
/// <c>AsJson(value, options)</c> of <see cref="IHttpRequestBuilder"/>.
/// </summary>
/// <remarks>
/// Extension methods, so the frozen interface keeps its shape and every implementor of it gets them.
/// </remarks>
public static class HttpRequestBodies
{
    /// <summary>
    /// Sends the value as a JSON body written by <paramref name="typeInfo"/>, which carries its own
    /// options: no reflection, so it works trimmed and under Native AOT.
    /// </summary>
    /// <param name="builder">The request being built.</param>
    /// <param name="value">The value to send.</param>
    /// <param name="typeInfo">The metadata to write it with, from a <c>JsonSerializerContext</c>.</param>
    /// <remarks>
    /// <see cref="IHttpRequestBuilder.JsonOptions"/> are left as they are: they keep applying to a
    /// response read by a reflection-based terminal.
    /// </remarks>
    public static IHttpRequestBuilder AsJson<TValue>(
        this IHttpRequestBuilder builder, TValue value, JsonTypeInfo<TValue> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(typeInfo);

        return builder.Content(JsonContent.Create(value, typeInfo));
    }
}
