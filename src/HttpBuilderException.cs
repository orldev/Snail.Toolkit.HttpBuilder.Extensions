using System.Net;

namespace Snail.Toolkit.HttpBuilder.Extensions;

/// <summary>
/// Thrown when a request completed but the response carried a failure status.
/// </summary>
/// <remarks>
/// The point of this type is <see cref="Body"/>: an API explains a rejection there, and
/// that explanation is gone by the time a deserialization error surfaces. Derives from
/// <see cref="HttpRequestException"/> so existing handlers keep catching it.
/// </remarks>
/// <param name="method">The method of the request that failed.</param>
/// <param name="requestUri">The URI of the request that failed.</param>
/// <param name="statusCode">The failure status the response carried.</param>
/// <param name="reasonPhrase">The reason phrase, if the server sent one.</param>
/// <param name="body">The response body, truncated; null if it could not be read.</param>
public sealed class HttpBuilderException(
    HttpMethod method,
    Uri? requestUri,
    HttpStatusCode statusCode,
    string? reasonPhrase,
    string? body)
    : HttpRequestException(Describe(method, requestUri, statusCode, reasonPhrase, body), null, statusCode)
{
    /// <summary>How much of the response body is kept in the message.</summary>
    internal const int BodyLimit = 4096;

    /// <summary>Gets the method of the request that failed.</summary>
    public HttpMethod Method { get; } = method;

    /// <summary>Gets the URI of the request that failed.</summary>
    public Uri? RequestUri { get; } = requestUri;

    /// <summary>Gets the reason phrase, if the server sent one.</summary>
    public string? ReasonPhrase { get; } = reasonPhrase;

    /// <summary>Gets the response body, truncated. Null if it could not be read.</summary>
    public string? Body { get; } = body;

    private static string Describe(
        HttpMethod method,
        Uri? requestUri,
        HttpStatusCode statusCode,
        string? reasonPhrase,
        string? body)
    {
        var reason = string.IsNullOrWhiteSpace(reasonPhrase) ? statusCode.ToString() : reasonPhrase;
        var message = $"{method} {requestUri} responded {(int)statusCode} {reason}.";

        return string.IsNullOrWhiteSpace(body) ? message : $"{message} Body: {body}";
    }
}
