using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Snail.Toolkit.HttpBuilder.Extensions;

/// <inheritdoc cref="IHttpRequestBuilder"/>
/// <remarks>One instance describes one request, which is what keeps state from leaking
/// into the next call on the same client. The first send marks the instance spent, and
/// every later send throws <see cref="InvalidOperationException"/>.</remarks>
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "A builder lives for one fluent chain and is never disposed by its caller; the content it may hold is in-memory or the caller's own, and IDisposable on it would be public API that every call site ignores.")]
public sealed class HttpRequestBuilder : IHttpRequestBuilder
{
    /// <summary>The characters of an HTTP field name, the <c>tchar</c> of RFC 9110.</summary>
    private static readonly SearchValues<char> FieldNameCharacters =
        SearchValues.Create("!#$%&'*+-.^_`|~0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz");

    /// <summary>The characters that end a header line on the wire.</summary>
    private static readonly SearchValues<char> LineBreaks = SearchValues.Create("\r\n\0");

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
    public JsonSerializerOptions JsonOptions => _jsonOptions ?? WebOptions;

    /// <summary>The timeout of the client this request is sent through.</summary>
    /// <remarks>
    /// <see cref="HttpClient.Timeout"/> covers a response read to its end only while the client reads it; a
    /// terminal that reads the body itself holds the rest of the request to it.
    /// </remarks>
    internal TimeSpan ClientTimeout => _client.Timeout;

    /// <summary>The web-style defaults, the options the reflection-based members read and write with.</summary>
    /// <remarks>
    /// <see cref="JsonSerializerOptions.Web"/> is marked for reflection because options are what
    /// reflection-based serialization runs with; handing them out serializes nothing, and every member
    /// that serializes with them carries the same marks.
    /// </remarks>
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
        Justification = "Only members marked RequiresUnreferencedCode serialize with these options.")]
    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
        Justification = "Only members marked RequiresDynamicCode serialize with these options.")]
    private static JsonSerializerOptions WebOptions => JsonSerializerOptions.Web;

    /// <inheritdoc/>
    /// <remarks>
    /// The body is not serialized here: content is created at send time from
    /// <see cref="JsonOptions"/>, so the options apply no matter which order they and
    /// the body were given in.
    /// </remarks>
    [RequiresUnreferencedCode(HttpRequestTerminals.ReflectionMessage)]
    [RequiresDynamicCode(HttpRequestTerminals.ReflectionMessage)]
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
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> is empty or not an HTTP field name, or <paramref name="value"/> holds a line break.
    /// </exception>
    /// <remarks>
    /// Both are checked here, at the call that gave them. Assembled at send time, an invalid name threw a
    /// <see cref="FormatException"/> from the body's headers or vanished from a request without a body, and a value
    /// is stored without validation: <c>"a\r\nX-Injected: b"</c> reached the socket as a header of its own.
    /// </remarks>
    public IHttpRequestBuilder Header(string name, string? value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        if (name.AsSpan().ContainsAnyExcept(FieldNameCharacters))
        {
            throw new ArgumentException($"'{name}' is not an HTTP header name.", nameof(name));
        }

        if (value is not null && value.AsSpan().ContainsAny(LineBreaks))
        {
            throw new ArgumentException($"The value of '{name}' holds a line break, which would end the header.", nameof(value));
        }

        _headers.Add(new KeyValuePair<string, string?>(name, value));

        return this;
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentException">A name is empty or not an HTTP field name.</exception>
    public IHttpRequestBuilder Headers(IEnumerable<KeyValuePair<string, string>> headers)
    {
        foreach ((string name, string value) in headers)
        {
            Header(name, value);
        }

        return this;
    }

    /// <inheritdoc/>
    public IHttpRequestBuilder Accept(string mediaType) => Header("Accept", mediaType);

    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> or <paramref name="value"/> holds half a surrogate pair.</exception>
    /// <remarks>
    /// Checked here, at the call: escaped at send time, a null name failed there, and half a surrogate pair was sent
    /// as U+FFFD without a word.
    /// </remarks>
    public IHttpRequestBuilder Query(string name, string? value)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (!IsWellFormed(name))
        {
            throw new ArgumentException("The query parameter's name holds half a surrogate pair.", nameof(name));
        }

        if (value is not null && !IsWellFormed(value))
        {
            throw new ArgumentException($"The value of the query parameter '{name}' holds half a surrogate pair.", nameof(value));
        }

        if (value is not null)
        {
            _query.Add(new KeyValuePair<string, string>(name, value));
        }

        return this;
    }

    /// <inheritdoc/>
    public IHttpRequestBuilder Query(IEnumerable<KeyValuePair<string, string?>> parameters)
    {
        foreach ((string name, string? value) in parameters)
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
        HttpRequestMessage message = CreateRequestMessage();

        MarkSent();

        return _client.SendAsync(message, completionOption, cancellationToken);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// A failure body is read under what is left of the client's <see cref="HttpClient.Timeout"/>: after
    /// <see cref="HttpCompletionOption.ResponseHeadersRead"/> the client no longer bounds the body, and an error
    /// answer that stalls held the call for as long as the server kept the connection.
    /// </remarks>
    public async Task<HttpResponseMessage> SendCheckedAsync(
        HttpCompletionOption completionOption, CancellationToken cancellationToken = default)
    {
        long started = Stopwatch.GetTimestamp();
        HttpResponseMessage response = await SendAsync(completionOption, cancellationToken).ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeLeft(started));

            string? body = await ReadCappedBodyAsync(response, deadline.Token, cancellationToken).ConfigureAwait(false);

            throw new HttpBuilderException(
                _method, CreateRequestUri(), response.StatusCode, response.ReasonPhrase, body);
        }
        finally
        {
            response.Dispose();
        }
    }

    /// <summary>What is left of the client's timeout since <paramref name="started"/>; infinite when the timeout is.</summary>
    private TimeSpan TimeLeft(long started)
    {
        if (_client.Timeout == Timeout.InfiniteTimeSpan)
        {
            return Timeout.InfiniteTimeSpan;
        }

        TimeSpan left = _client.Timeout - Stopwatch.GetElapsedTime(started);

        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    /// <summary>Assembles the message.</summary>
    /// <remarks>
    /// A header the request collection rejects belongs to the content, and lands there as
    /// a replacement rather than an addition: the body already carries a
    /// <c>Content-Type</c>, and naming one means overriding it. The HTTP version and
    /// policy default to what the client is configured with, so a typed client set up
    /// for HTTP/2 is not silently downgraded.
    /// </remarks>
    private HttpRequestMessage CreateRequestMessage()
    {
        var message = new HttpRequestMessage(_method, CreateRequestUri())
        {
            Content = _hasJsonBody ? CreateJsonBody() : _content,
            Version = _version ?? _client.DefaultRequestVersion,
            VersionPolicy = _versionPolicy ?? _client.DefaultVersionPolicy
        };

        foreach ((string name, string? value) in _headers)
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

    /// <summary>Writes the body <see cref="AsJson{TValue}"/> was given, with the options in force at send time.</summary>
    /// <remarks>
    /// Only <see cref="AsJson{TValue}"/> sets a JSON body, and it carries the reflection marks, so its
    /// caller was told; the body is written here, at send time, so that options given after it still apply.
    /// </remarks>
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
        Justification = "A JSON body is only set by AsJson, which is marked RequiresUnreferencedCode.")]
    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
        Justification = "A JSON body is only set by AsJson, which is marked RequiresDynamicCode.")]
    private JsonContent CreateJsonBody() =>
        JsonContent.Create(_jsonBody, _jsonBodyType!, mediaType: null, JsonOptions);

    /// <summary>
    /// Marks the one send this builder describes, so a second send fails loudly instead
    /// of sending its content again: a stream handed to <see cref="Content"/> can be read
    /// only once.
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
    /// endless, on a streaming endpoint — error response cannot exhaust memory, and under
    /// <paramref name="deadline"/>, so a stalled one cannot hold the call. An unreadable or
    /// late body yields null: the status is the news, and a failed read must not mask it;
    /// only the caller's own cancellation propagates. The cut never ends on half of a
    /// surrogate pair, which a log writer would turn into U+FFFD or refuse.
    /// </remarks>
    private static async Task<string?> ReadCappedBodyAsync(
        HttpResponseMessage response, CancellationToken deadline, CancellationToken cancellationToken)
    {
        try
        {
            await using Stream stream = await response.Content
                .ReadAsStreamAsync(deadline)
                .ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            char[] buffer = new char[HttpBuilderException.BodyLimit + 1];
            int filled = 0;

            while (filled < buffer.Length)
            {
                int read = await reader
                    .ReadAsync(buffer.AsMemory(filled), deadline)
                    .ConfigureAwait(false);

                if (read == 0)
                {
                    break;
                }

                filled += read;
            }

            if (filled <= HttpBuilderException.BodyLimit)
            {
                return new string(buffer, 0, filled);
            }

            int kept = char.IsHighSurrogate(buffer[HttpBuilderException.BodyLimit - 1])
                ? HttpBuilderException.BodyLimit - 1
                : HttpBuilderException.BodyLimit;

            return $"{new string(buffer, 0, kept)}…";
        }
        catch (OperationCanceledException exception)
            when (cancellationToken.IsCancellationRequested && exception.CancellationToken != cancellationToken)
        {
            throw new TaskCanceledException(exception.Message, exception, cancellationToken);
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
        string path = AppendQuery(_path);

        if (Uri.TryCreate(path, UriKind.Absolute, out Uri? absolute)
            && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
        {
            return absolute;
        }

        Uri baseAddress = _client.BaseAddress
            ?? throw new InvalidOperationException(
                $"'{path}' is relative, so {nameof(HttpClient)}.{nameof(HttpClient.BaseAddress)} "
                + "must be set. Give the client a base address or pass an absolute URL.");

        string authority = baseAddress.GetLeftPart(UriPartial.Authority);

        if (path.StartsWith('/'))
        {
            return new Uri($"{authority}{path}", UriKind.Absolute);
        }

        string basePath = baseAddress.AbsolutePath.TrimEnd('/');

        return new Uri($"{authority}{basePath}/{path}", UriKind.Absolute);
    }

    /// <summary>Whether the text is whole UTF-16, every surrogate in a pair, so escaping it alters nothing.</summary>
    private static bool IsWellFormed(ReadOnlySpan<char> text)
    {
        if (!text.ContainsAnyInRange('\uD800', '\uDFFF'))
        {
            return true;
        }

        while (!text.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(text, out _, out int used) != OperationStatus.Done)
            {
                return false;
            }

            text = text[used..];
        }

        return true;
    }

    /// <summary>The path with the query parameters, placed before any fragment.</summary>
    /// <remarks>
    /// Appended after a fragment, the query was part of the fragment, which never leaves the client: the parameters
    /// were dropped without a word.
    /// </remarks>
    private string AppendQuery(string path)
    {
        if (_query.Count == 0)
        {
            return path;
        }

        int fragmentStart = path.IndexOf('#', StringComparison.Ordinal);
        string beforeFragment = fragmentStart < 0 ? path : path[..fragmentStart];
        string fragment = fragmentStart < 0 ? string.Empty : path[fragmentStart..];

        var uri = new StringBuilder(beforeFragment);
        char separator = beforeFragment.Contains('?', StringComparison.Ordinal) ? '&' : '?';

        foreach ((string name, string value) in _query)
        {
            uri.Append(separator)
                .Append(Uri.EscapeDataString(name))
                .Append('=')
                .Append(Uri.EscapeDataString(value));

            separator = '&';
        }

        uri.Append(fragment);

        return uri.ToString();
    }
}
