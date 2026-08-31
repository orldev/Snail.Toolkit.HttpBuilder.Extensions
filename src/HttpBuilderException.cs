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
public sealed class HttpBuilderException : HttpRequestException
{
    /// <summary>How much of the response body is kept in the message.</summary>
    internal const int BodyLimit = 4096;

    /// <summary>Initializes a new instance of the <see cref="HttpBuilderException"/> class.</summary>
    public HttpBuilderException(
        HttpMethod method,
        Uri? requestUri,
        HttpStatusCode statusCode,
        string? reasonPhrase,
        string? body)
        : base(Describe(method, requestUri, statusCode, reasonPhrase, body), null, statusCode)
    {
        Method = method;
        RequestUri = requestUri;
        ReasonPhrase = reasonPhrase;
        Body = body;
    }

    /// <summary>Gets the method of the request that failed.</summary>
    public HttpMethod Method { get; }

    /// <summary>Gets the URI of the request that failed.</summary>
    public Uri? RequestUri { get; }

    /// <summary>Gets the reason phrase, if the server sent one.</summary>
    public string? ReasonPhrase { get; }

    /// <summary>Gets the response body, truncated. Null if it could not be read.</summary>
    public string? Body { get; }

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
