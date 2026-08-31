using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Snail.Toolkit.HttpBuilder.Extensions;

/// <inheritdoc cref="IHttpRequestBuilder"/>
/// <remarks>One instance describes one request, which is what keeps state from leaking
/// into the next call on the same client.</remarks>
public sealed class HttpRequestBuilder : IHttpRequestBuilder
{
    private readonly HttpClient _client;
    private readonly HttpMethod _method;
    private readonly string _path;
    private readonly List<KeyValuePair<string, string?>> _headers = [];
    private readonly List<KeyValuePair<string, string>> _query = [];

    private HttpContent? _content;
    private JsonSerializerOptions? _jsonOptions;
    private Version? _version;
    private HttpVersionPolicy? _versionPolicy;

    /// <summary>Initializes a new instance of the <see cref="HttpRequestBuilder"/> class.</summary>
    public HttpRequestBuilder(HttpClient client, HttpMethod method, string path)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(path);

        _client = client;
        _method = method;
        _path = path;
    }

    /// <inheritdoc/>
    public IHttpRequestBuilder AsJson<TValue>(TValue value, JsonSerializerOptions? options = null)
    {
        _jsonOptions ??= options;
        _content = JsonContent.Create(value, mediaType: null, options);

        return this;
    }

    /// <inheritdoc/>
    public IHttpRequestBuilder AsString(string value, Encoding? encoding = null, string? mediaType = null)
    {
        // UTF-8 rather than Encoding.Default, which is the OS's guess, not the wire's.
        _content = new StringContent(value, encoding ?? Encoding.UTF8, mediaType ?? "text/plain");

        return this;
    }

    /// <inheritdoc/>
    public IHttpRequestBuilder AsForm(IEnumerable<KeyValuePair<string, string>> fields)
    {
        _content = new FormUrlEncodedContent(fields);

        return this;
    }

    /// <inheritdoc/>
    public IHttpRequestBuilder Content(HttpContent? content)
    {
        _content = content;

        return this;
    }

    /// <inheritdoc/>
    public IHttpRequestBuilder Header(string name, string? value)
    {
        _headers.Add(new KeyValuePair<string, string?>(name, value));

        return this;
    }

    /// <inheritdoc/>
    public IHttpRequestBuilder Headers(IEnumerable<KeyValuePair<string, string>> headers)
    {
        foreach (var (name, value) in headers)
        {
            _headers.Add(new KeyValuePair<string, string?>(name, value));
        }

        return this;
    }

    /// <inheritdoc/>
    public IHttpRequestBuilder Accept(string mediaType) => Header("Accept", mediaType);

    /// <inheritdoc/>
    public IHttpRequestBuilder Query(string name, string? value)
    {
        if (value is not null)
        {
            _query.Add(new KeyValuePair<string, string>(name, value));
        }

        return this;
    }

    /// <inheritdoc/>
    public IHttpRequestBuilder Query(IEnumerable<KeyValuePair<string, string?>> parameters)
    {
        foreach (var (name, value) in parameters)
        {
            Query(name, value);
        }

        return this;
    }

    /// <inheritdoc/>
    public IHttpRequestBuilder WithVersion(Version version)
    {
        _version = version;

        return this;
    }

    /// <inheritdoc/>
    public IHttpRequestBuilder WithVersionPolicy(HttpVersionPolicy policy)
    {
        _versionPolicy = policy;

        return this;
    }

    /// <inheritdoc/>
    public IHttpRequestBuilder WithJsonOptions(JsonSerializerOptions? options)
    {
        _jsonOptions = options;

        return this;
    }

    /// <inheritdoc/>
    public Task<HttpResponseMessage> SendAsync(CancellationToken cancellationToken = default) =>
        _client.SendAsync(CreateRequestMessage(), cancellationToken);

    /// <inheritdoc/>
    public async Task<TValue?> SendAsync<TValue>(CancellationToken cancellationToken = default)
    {
        using var response = await SendCheckedAsync(
            HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);

        if (IsEmpty(response))
        {
            return default;
        }

        return await response.Content
            .ReadFromJsonAsync<TValue>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<string> SendAsStringAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendCheckedAsync(
            HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);

        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<TValue> SendAsNdjsonAsync<TValue>(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var response = await SendCheckedAsync(
            HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        // Read to null, never through EndOfStream: on a live network stream EndOfStream
        // blocks the thread with a synchronous read while it waits for the next chunk.
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var value = JsonSerializer.Deserialize<TValue>(line, JsonOptions);

            if (value is not null)
            {
                yield return value;
            }
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<TValue> SendAsJsonStreamAsync<TValue>(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var response = await SendCheckedAsync(
            HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);

        await foreach (var value in JsonSerializer
                           .DeserializeAsyncEnumerable<TValue>(stream, JsonOptions, cancellationToken)
                           .ConfigureAwait(false))
        {
            if (value is not null)
            {
                yield return value;
            }
        }
    }

    /// <summary>
    /// The options every typed terminal reads with. Web-style by default, the same
    /// convention <see cref="HttpClientJsonExtensions"/> uses.
    /// </summary>
    private JsonSerializerOptions JsonOptions => _jsonOptions ?? JsonSerializerOptions.Web;

    /// <summary>
    /// Sends the request and verifies the status. Streaming callers pass
    /// <see cref="HttpCompletionOption.ResponseHeadersRead"/> so the body can be read
    /// while the server is still generating it. The caller disposes the response.
    /// </summary>
    private async Task<HttpResponseMessage> SendCheckedAsync(
        HttpCompletionOption completionOption, CancellationToken cancellationToken)
    {
        var request = CreateRequestMessage();
        var requestUri = request.RequestUri;

        var response = await _client
            .SendAsync(request, completionOption, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await EnsureSuccessAsync(response, requestUri, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            response.Dispose();
            throw;
        }

        return response;
    }

    /// <summary>Assembles the message. Internal so tests can inspect it.</summary>
    internal HttpRequestMessage CreateRequestMessage()
    {
        var message = new HttpRequestMessage(_method, CreateRequestUri())
        {
            Content = _content,
            Version = _version ?? HttpVersion.Version11,
            VersionPolicy = _versionPolicy ?? HttpVersionPolicy.RequestVersionOrLower
        };

        foreach (var (name, value) in _headers)
        {
            if (message.Headers.TryAddWithoutValidation(name, value))
            {
                continue;
            }

            // Rejected above because they belong to the content. Replace rather than
            // add: the body already has a Content-Type, and naming one means overriding it.
            if (message.Content is not null)
            {
                message.Content.Headers.Remove(name);
                message.Content.Headers.TryAddWithoutValidation(name, value);
            }
        }

        return message;
    }

    /// <summary>Resolves the request URI against the client's base address.</summary>
    /// <remarks>
    /// Joined by hand: <see cref="UriBuilder.Path"/> would discard the path already on the
    /// base address, and relative-URI parsing reads a segment ending in a colon as a
    /// scheme, mangling paths like <c>v1/places:autocomplete</c>.
    /// </remarks>
    private Uri CreateRequestUri()
    {
        var path = AppendQuery(_path);

        if (Uri.TryCreate(path, UriKind.Absolute, out var absolute)
            && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
        {
            return absolute;
        }

        var baseAddress = _client.BaseAddress
            ?? throw new InvalidOperationException(
                $"'{path}' is relative, so {nameof(HttpClient)}.{nameof(HttpClient.BaseAddress)} "
                + "must be set. Give the client a base address or pass an absolute URL.");

        var authority = baseAddress.GetLeftPart(UriPartial.Authority);

        // Root-relative replaces the base path, as HttpClient and browsers read it.
        if (path.StartsWith('/'))
        {
            return new Uri($"{authority}{path}", UriKind.Absolute);
        }

        var basePath = baseAddress.AbsolutePath.TrimEnd('/');

        return new Uri($"{authority}{basePath}/{path}", UriKind.Absolute);
    }

    private string AppendQuery(string path)
    {
        if (_query.Count == 0)
        {
            return path;
        }

        var query = string.Join(
            '&',
            _query.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));

        return path.Contains('?') ? $"{path}&{query}" : $"{path}?{query}";
    }

    /// <summary>Turns a failure status into an exception carrying the response body.</summary>
    private async Task EnsureSuccessAsync(
        HttpResponseMessage response, Uri? requestUri, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string? body = null;

        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (body.Length > HttpBuilderException.BodyLimit)
            {
                body = body[..HttpBuilderException.BodyLimit] + "…";
            }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // The status is the news; an unreadable body must not mask it.
        }

        throw new HttpBuilderException(
            _method, requestUri, response.StatusCode, response.ReasonPhrase, body);
    }

    /// <summary>Whether the response carries no body, so <c>default</c> beats a parse error.</summary>
    private static bool IsEmpty(HttpResponseMessage response) =>
        response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.ResetContent
        || response.Content.Headers.ContentLength == 0;
}
