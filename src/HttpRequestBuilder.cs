using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Snail.Toolkit.HttpBuilder.Extensions;

/// <inheritdoc cref="IHttpRequestBuilder"/>
/// <remarks>One instance describes one request, which is what keeps state from leaking
/// into the next call on the same client. The first send marks the instance spent, and
/// every later send throws <see cref="InvalidOperationException"/>.</remarks>
public sealed class HttpRequestBuilder : IHttpRequestBuilder
{
    private readonly HttpClient _client;
    private readonly HttpMethod _method;
    private readonly string _path;
    private readonly List<KeyValuePair<string, string?>> _headers = [];
    private readonly List<KeyValuePair<string, string>> _query = [];

    private HttpContent? _content;
    private object? _jsonBody;
    private Type? _jsonBodyType;
    private bool _hasJsonBody;
    private JsonSerializerOptions? _jsonOptions;
    private Version? _version;
    private HttpVersionPolicy? _versionPolicy;
    private int _sent;

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
    /// <remarks>
    /// Web-style by default, the same convention
    /// <see cref="HttpClientJsonExtensions"/> uses.
    /// </remarks>
    public JsonSerializerOptions JsonOptions => _jsonOptions ?? JsonSerializerOptions.Web;

    /// <inheritdoc/>
    /// <remarks>
    /// The body is not serialized here: content is created at send time from
    /// <see cref="JsonOptions"/>, so the options apply no matter which order they and
    /// the body were given in.
    /// </remarks>
    public IHttpRequestBuilder AsJson<TValue>(TValue value, JsonSerializerOptions? options = null)
    {
        if (options is not null)
        {
            _jsonOptions = options;
        }

        _content = null;
        _jsonBody = value;
        _jsonBodyType = typeof(TValue);
        _hasJsonBody = true;

        return this;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Defaults to UTF-8 rather than <see cref="Encoding.Default"/>, which is the OS's
    /// guess, not the wire's.
    /// </remarks>
    public IHttpRequestBuilder AsString(string value, Encoding? encoding = null, string? mediaType = null)
    {
        _hasJsonBody = false;
        _content = new StringContent(value, encoding ?? Encoding.UTF8, mediaType ?? "text/plain");

        return this;
    }

    /// <inheritdoc/>
    public IHttpRequestBuilder AsForm(IEnumerable<KeyValuePair<string, string>> fields)
    {
        _hasJsonBody = false;
        _content = new FormUrlEncodedContent(fields);

        return this;
    }

    /// <inheritdoc/>
    public IHttpRequestBuilder Content(HttpContent? content)
    {
        _hasJsonBody = false;
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
        SendAsync(HttpCompletionOption.ResponseContentRead, cancellationToken);

    /// <inheritdoc/>
    public Task<HttpResponseMessage> SendAsync(
        HttpCompletionOption completionOption, CancellationToken cancellationToken = default)
    {
        var message = CreateRequestMessage();

        MarkSent();

        return _client.SendAsync(message, completionOption, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<HttpResponseMessage> SendCheckedAsync(
        HttpCompletionOption completionOption, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(completionOption, cancellationToken).ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        try
        {
            var body = await ReadCappedBodyAsync(response, cancellationToken).ConfigureAwait(false);

            throw new HttpBuilderException(
                _method, CreateRequestUri(), response.StatusCode, response.ReasonPhrase, body);
        }
        finally
        {
            response.Dispose();
        }
    }

    /// <summary>Assembles the message. Internal so tests can inspect it.</summary>
    /// <remarks>
    /// A header the request collection rejects belongs to the content, and lands there as
    /// a replacement rather than an addition: the body already carries a
    /// <c>Content-Type</c>, and naming one means overriding it. The HTTP version and
    /// policy default to what the client is configured with, so a typed client set up
    /// for HTTP/2 is not silently downgraded.
    /// </remarks>
    internal HttpRequestMessage CreateRequestMessage()
    {
        var message = new HttpRequestMessage(_method, CreateRequestUri())
        {
            Content = _hasJsonBody
                ? JsonContent.Create(_jsonBody, _jsonBodyType!, mediaType: null, JsonOptions)
                : _content,
            Version = _version ?? _client.DefaultRequestVersion,
            VersionPolicy = _versionPolicy ?? _client.DefaultVersionPolicy
        };

        foreach (var (name, value) in _headers)
        {
            if (message.Headers.TryAddWithoutValidation(name, value))
            {
                continue;
            }

            if (message.Content is not null)
            {
                message.Content.Headers.Remove(name);
                message.Content.Headers.TryAddWithoutValidation(name, value);
            }
        }

        return message;
    }

    /// <summary>
    /// Marks the one send this builder describes, so a second send fails loudly instead
    /// of reusing content the first send already disposed.
    /// </summary>
    /// <remarks>
    /// Set atomically, so the guard holds even for two racing sends, and only after the
    /// message was assembled, so a failed assembly does not brand an unsent builder as
    /// sent.
    /// </remarks>
    private void MarkSent()
    {
        if (Interlocked.Exchange(ref _sent, 1) == 1)
        {
            throw new InvalidOperationException(
                "This builder has already sent its request. A builder describes one "
                + "request; start the next call from a verb on the client.");
        }
    }

    /// <summary>Reads at most <see cref="HttpBuilderException.BodyLimit"/> characters of a failure body.</summary>
    /// <remarks>
    /// The cap is applied at the stream, not after buffering, so an oversized — or
    /// endless, on a streaming endpoint — error response cannot exhaust memory. An
    /// unreadable body yields null: the status is the news, and a failed read must not
    /// mask it.
    /// </remarks>
    private static async Task<string?> ReadCappedBodyAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            var buffer = new char[HttpBuilderException.BodyLimit + 1];
            var filled = 0;

            while (filled < buffer.Length)
            {
                var read = await reader
                    .ReadAsync(buffer.AsMemory(filled), cancellationToken)
                    .ConfigureAwait(false);

                if (read == 0)
                {
                    break;
                }

                filled += read;
            }

            return filled > HttpBuilderException.BodyLimit
                ? $"{new string(buffer, 0, HttpBuilderException.BodyLimit)}…"
                : new string(buffer, 0, filled);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Resolves the request URI against the client's base address.</summary>
    /// <remarks>
    /// Joined by hand: <see cref="UriBuilder.Path"/> would discard the path already on the
    /// base address, and relative-URI parsing reads a segment ending in a colon as a
    /// scheme, mangling paths like <c>v1/places:autocomplete</c>. A root-relative path
    /// replaces the base path, as <see cref="HttpClient"/> and browsers read it.
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
}
