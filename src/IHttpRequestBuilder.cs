using System.Text;
using System.Text.Json;

namespace Snail.Toolkit.HttpBuilder.Extensions;

/// <summary>
/// One HTTP request under construction. Configuration methods return the same instance so
/// calls chain; the <c>SendAsync</c> members are the terminal operations.
/// </summary>
public interface IHttpRequestBuilder
{
    /// <summary>Sends the value as a JSON body. The options are reused to read the response.</summary>
    IHttpRequestBuilder AsJson<TValue>(TValue value, JsonSerializerOptions? options = null);

    /// <summary>Sends a string body. Defaults to UTF-8 and <c>text/plain</c>.</summary>
    IHttpRequestBuilder AsString(string value, Encoding? encoding = null, string? mediaType = null);

    /// <summary>Sends the fields as an <c>application/x-www-form-urlencoded</c> body.</summary>
    IHttpRequestBuilder AsForm(IEnumerable<KeyValuePair<string, string>> fields);

    /// <summary>Sends a body prepared by the caller.</summary>
    IHttpRequestBuilder Content(HttpContent? content);

    /// <summary>Adds one header. Content headers such as <c>Content-Type</c> go to the body.</summary>
    IHttpRequestBuilder Header(string name, string? value);

    /// <summary>Adds several headers, keeping any already set.</summary>
    IHttpRequestBuilder Headers(IEnumerable<KeyValuePair<string, string>> headers);

    /// <summary>Sets the <c>Accept</c> header.</summary>
    IHttpRequestBuilder Accept(string mediaType);

    /// <summary>
    /// Appends an escaped query parameter. A null value is skipped, so an optional filter
    /// needs no branching at the call site; an empty string is a real value.
    /// </summary>
    IHttpRequestBuilder Query(string name, string? value);

    /// <summary>Appends several query parameters, skipping null values.</summary>
    IHttpRequestBuilder Query(IEnumerable<KeyValuePair<string, string?>> parameters);

    /// <summary>Sets the HTTP version.</summary>
    IHttpRequestBuilder WithVersion(Version version);

    /// <summary>Sets how strictly the HTTP version is applied.</summary>
    IHttpRequestBuilder WithVersionPolicy(HttpVersionPolicy policy);

    /// <summary>Sets the serializer options used to read the response.</summary>
    IHttpRequestBuilder WithJsonOptions(JsonSerializerOptions? options);

    /// <summary>
    /// Sends the request and returns the raw response, whatever its status. The caller
    /// owns it and must dispose it.
    /// </summary>
    Task<HttpResponseMessage> SendAsync(CancellationToken cancellationToken = default);

    /// <summary>Sends the request and deserializes a successful JSON response.</summary>
    /// <returns>The body, or <c>default</c> when the response carries none.</returns>
    /// <exception cref="HttpBuilderException">The status was not a success.</exception>
    Task<TValue?> SendAsync<TValue>(CancellationToken cancellationToken = default);

    /// <summary>Sends the request and returns a successful response as text.</summary>
    /// <exception cref="HttpBuilderException">The status was not a success.</exception>
    Task<string> SendAsStringAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends the request and streams a successful newline-delimited JSON response,
    /// yielding each line as it arrives rather than waiting for the body to end.
    /// Blank lines and JSON <c>null</c>s are skipped.
    /// </summary>
    /// <exception cref="HttpBuilderException">The status was not a success.</exception>
    IAsyncEnumerable<TValue> SendAsNdjsonAsync<TValue>(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends the request and streams the elements of a successful JSON-array response,
    /// yielding each element as it arrives rather than waiting for the closing bracket.
    /// JSON <c>null</c> elements are skipped.
    /// </summary>
    /// <exception cref="HttpBuilderException">The status was not a success.</exception>
    IAsyncEnumerable<TValue> SendAsJsonStreamAsync<TValue>(CancellationToken cancellationToken = default);
}
