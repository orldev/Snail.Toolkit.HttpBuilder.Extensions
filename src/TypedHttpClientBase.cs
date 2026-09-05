namespace Snail.Toolkit.HttpBuilder.Extensions;

/// <summary>
/// Base class for a typed <see cref="HttpClient"/> wrapper. Each verb starts a new
/// <see cref="HttpRequestBuilder"/>, which is configured fluently and then sent.
/// </summary>
/// <param name="client">
/// The client the requests go through. Paths are resolved against its
/// <see cref="HttpClient.BaseAddress"/>.
/// </param>
/// <remarks>
/// Holds no request state of its own, so a derived client is safe to reuse and to call
/// concurrently: two calls cannot bleed a header, a body or a path into one another.
/// </remarks>
/// <example>
/// <code>
/// public sealed class PaymentsClient(HttpClient client) : TypedHttpClientBase(client)
/// {
///     public Task&lt;Token?&gt; ExchangeAsync(Grant grant, CancellationToken ct = default) =&gt;
///         Post("v1/oauth2/token")
///             .AsJson(grant)
///             .Header("Authorization", "Basic 12345")
///             .SendAsync&lt;Token&gt;(ct);
/// }
/// </code>
/// </example>
public abstract class TypedHttpClientBase(HttpClient client)
{
    /// <summary>Starts a <c>GET</c> request.</summary>
    protected IHttpRequestBuilder Get(string path) => Request(HttpMethod.Get, path);

    /// <summary>Starts a <c>POST</c> request.</summary>
    protected IHttpRequestBuilder Post(string path) => Request(HttpMethod.Post, path);

    /// <summary>Starts a <c>PUT</c> request.</summary>
    protected IHttpRequestBuilder Put(string path) => Request(HttpMethod.Put, path);

    /// <summary>Starts a <c>PATCH</c> request.</summary>
    protected IHttpRequestBuilder Patch(string path) => Request(HttpMethod.Patch, path);

    /// <summary>Starts a <c>DELETE</c> request.</summary>
    protected IHttpRequestBuilder Delete(string path) => Request(HttpMethod.Delete, path);

    /// <summary>Starts a <c>HEAD</c> request.</summary>
    protected IHttpRequestBuilder Head(string path) => Request(HttpMethod.Head, path);

    /// <summary>Starts an <c>OPTIONS</c> request.</summary>
    protected IHttpRequestBuilder Options(string path) => Request(HttpMethod.Options, path);

    /// <summary>Starts a <c>TRACE</c> request.</summary>
    protected IHttpRequestBuilder Trace(string path) => Request(HttpMethod.Trace, path);

    /// <summary>Starts a request with an arbitrary method.</summary>
    /// <param name="method">The method to use.</param>
    /// <param name="path">Relative to the client's base address, or an absolute URL.</param>
    protected IHttpRequestBuilder Request(HttpMethod method, string path) =>
        new HttpRequestBuilder(client, method, path);
}
