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
public static partial class HttpRequestTerminals
{
    /// <summary>Why the terminals that read with <see cref="IHttpRequestBuilder.JsonOptions"/> are not native.</summary>
    internal const string ReflectionMessage =
        "Reads JSON with IHttpRequestBuilder.JsonOptions, which resolves types by reflection. "
        + "Trimmed and Native AOT apps pass a JsonTypeInfo<T> from a JsonSerializerContext instead.";

    /// <summary>Why each released reflection overload keeps its optional token beside a longer overload.</summary>
    private const string ReleasedSignature =
        "Released in 0.3.0 with an optional token: dropping the default would break every caller that omits it. "
        + "The JsonTypeInfo<T> overload beside it takes a required token, so no call binds differently.";
}
