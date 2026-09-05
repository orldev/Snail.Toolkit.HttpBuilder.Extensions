using System.Text;
using System.Text.Json;

namespace Snail.Toolkit.HttpBuilder.Extensions;

/// <summary>
/// One HTTP request under construction. Configuration methods return the same instance so
/// calls chain; the send members are the terminal operations.
/// </summary>
/// <remarks>
/// <para>
/// This interface is frozen: it carries configuration and the raw and checked sends.
/// Response formats — JSON, NDJSON, JSON array, SSE — live in
/// <see cref="HttpRequestTerminals"/> as extension methods over the checked send, so a
/// new format never breaks an implementor of this contract.
/// </para>
/// <para>
/// A builder is single-use: the send disposes the request content, so a second send on
/// the same instance throws <see cref="InvalidOperationException"/> rather than reusing
/// what is gone. Start the next call from a verb on the client.
/// </para>
/// </remarks>
public interface IHttpRequestBuilder
{
    /// <summary>
    /// The serializer options the JSON body is written with and the response is read
    /// with. Web-style until <see cref="AsJson{TValue}"/> or
    /// <see cref="WithJsonOptions"/> sets them; the options given last win.
    /// </summary>
    JsonSerializerOptions JsonOptions { get; }

    /// <summary>
    /// Sends the value as a JSON body, serialized at send time with
    /// <see cref="JsonOptions"/>. Options given here become the builder's options for
    /// both writing the body and reading the response.
    /// </summary>
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

    /// <summary>
    /// Sets the HTTP version. Unset, the client's
    /// <see cref="HttpClient.DefaultRequestVersion"/> applies.
    /// </summary>
    IHttpRequestBuilder WithVersion(Version version);

    /// <summary>
    /// Sets how strictly the HTTP version is applied. Unset, the client's
    /// <see cref="HttpClient.DefaultVersionPolicy"/> applies.
    /// </summary>
    IHttpRequestBuilder WithVersionPolicy(HttpVersionPolicy policy);

    /// <summary>Sets <see cref="JsonOptions"/> for both the body and the response.</summary>
    IHttpRequestBuilder WithJsonOptions(JsonSerializerOptions? options);

    /// <summary>
    /// Sends the request and returns the raw response, whatever its status. The caller
    /// owns it and must dispose it.
    /// </summary>
    Task<HttpResponseMessage> SendAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends the request with the given completion option and returns the raw response,
    /// whatever its status. The caller owns it and must dispose it.
    /// </summary>
    Task<HttpResponseMessage> SendAsync(
        HttpCompletionOption completionOption, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends the request and verifies the status, returning the successful response.
    /// Streaming callers pass <see cref="HttpCompletionOption.ResponseHeadersRead"/> so
    /// the body can be read while the server is still generating it. The caller disposes
    /// the response.
    /// </summary>
    /// <exception cref="HttpBuilderException">
    /// The status was not a success. The body the exception carries is read capped, so an
    /// oversized error response cannot exhaust memory.
    /// </exception>
    Task<HttpResponseMessage> SendCheckedAsync(
        HttpCompletionOption completionOption, CancellationToken cancellationToken = default);
}
