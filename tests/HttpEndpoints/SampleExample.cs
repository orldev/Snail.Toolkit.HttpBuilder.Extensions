using Snail.Toolkit.HttpBuilder.Extensions.Tests.Contracts;

namespace Snail.Toolkit.HttpBuilder.Extensions.Tests.HttpEndpoints;

/// <summary>
/// A client shaped like a real one, used to exercise the builder through the same
/// surface a consumer sees.
/// </summary>
public class SampleExample(HttpClient client) : HttpBuilder(client)
{
    /// <summary>Sends a body, headers and an explicit version, and reads a typed response.</summary>
    public Task<Response?> Run(
        HttpMethod method,
        string path,
        Request request,
        Dictionary<string, string> headers,
        Version version,
        HttpVersionPolicy versionPolicy,
        CancellationToken cancellationToken = default) =>
        Request(method, path)
            .AsJson(request)
            .Headers(headers)
            .WithVersion(version)
            .WithVersionPolicy(versionPolicy)
            .SendAsync<Response>(cancellationToken);

    /// <summary>Sends a bodiless request and returns the raw response.</summary>
    public Task<HttpResponseMessage> Run(
        HttpMethod method, string path, CancellationToken cancellationToken = default) =>
        Request(method, path).SendAsync(cancellationToken);

    /// <summary>Reads a typed response with no body, headers or version set.</summary>
    public Task<Response?> Get(string path, CancellationToken cancellationToken = default) =>
        base.Get(path).SendAsync<Response>(cancellationToken);

    /// <summary>Sends a query string.</summary>
    public Task<Response?> Search(
        string path,
        IEnumerable<KeyValuePair<string, string?>> parameters,
        CancellationToken cancellationToken = default) =>
        base.Get(path).Query(parameters).SendAsync<Response>(cancellationToken);

    /// <summary>Streams newline-delimited JSON chunks, as an AI-style endpoint sends them.</summary>
    public IAsyncEnumerable<Response> Stream(
        string path, Request request, CancellationToken cancellationToken = default) =>
        base.Post(path).AsJson(request).SendAsNdjsonAsync<Response>(cancellationToken);

    /// <summary>Streams the elements of a JSON-array response.</summary>
    public IAsyncEnumerable<Response> StreamArray(
        string path, CancellationToken cancellationToken = default) =>
        base.Get(path).SendAsJsonStreamAsync<Response>(cancellationToken);

    /// <summary>Posts a body, so a following call can be checked for not inheriting it.</summary>
    public Task<Response?> Post(
        string path, Request request, string header, CancellationToken cancellationToken = default) =>
        base.Post(path)
            .AsJson(request)
            .Header("X-Sample", header)
            .SendAsync<Response>(cancellationToken);
}
