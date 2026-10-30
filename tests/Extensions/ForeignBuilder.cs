using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;

namespace Snail.Toolkit.HttpBuilder.Extensions.Tests.Extensions;

/// <summary>
/// A builder of another implementation than the library's: it answers the checked send with the response it is
/// given and records how it was asked, so a terminal can be seen working through the interface alone.
/// </summary>
/// <param name="answer">Makes the response of each checked send.</param>
public sealed class ForeignBuilder(Func<HttpResponseMessage> answer) : IHttpRequestBuilder
{
    private const string NotUsed = "A terminal reaches a foreign builder through its checked send only.";

    /// <summary>The completion option and token of every checked send, in order.</summary>
    public List<(HttpCompletionOption Option, CancellationToken Token)> CheckedSends { get; } = [];

    /// <inheritdoc/>
    public JsonSerializerOptions JsonOptions => JsonSerializerOptions.Web;

    /// <inheritdoc/>
    public Task<HttpResponseMessage> SendCheckedAsync(
        HttpCompletionOption completionOption, CancellationToken cancellationToken = default)
    {
        CheckedSends.Add((completionOption, cancellationToken));

        return Task.FromResult(answer());
    }

    /// <inheritdoc/>
    [RequiresUnreferencedCode(NotUsed)]
    [RequiresDynamicCode(NotUsed)]
    public IHttpRequestBuilder AsJson<TValue>(TValue value, JsonSerializerOptions? options = null) =>
        throw new NotSupportedException(NotUsed);

    /// <inheritdoc/>
    public IHttpRequestBuilder AsString(string value, Encoding? encoding = null, string? mediaType = null) =>
        throw new NotSupportedException(NotUsed);

    /// <inheritdoc/>
    public IHttpRequestBuilder AsForm(IEnumerable<KeyValuePair<string, string>> fields) =>
        throw new NotSupportedException(NotUsed);

    /// <inheritdoc/>
    public IHttpRequestBuilder Content(HttpContent? content) => throw new NotSupportedException(NotUsed);

    /// <inheritdoc/>
    public IHttpRequestBuilder Header(string name, string? value) => throw new NotSupportedException(NotUsed);

    /// <inheritdoc/>
    public IHttpRequestBuilder Headers(IEnumerable<KeyValuePair<string, string>> headers) =>
        throw new NotSupportedException(NotUsed);

    /// <inheritdoc/>
    public IHttpRequestBuilder Accept(string mediaType) => throw new NotSupportedException(NotUsed);

    /// <inheritdoc/>
    public IHttpRequestBuilder Query(string name, string? value) => throw new NotSupportedException(NotUsed);

    /// <inheritdoc/>
    public IHttpRequestBuilder Query(IEnumerable<KeyValuePair<string, string?>> parameters) =>
        throw new NotSupportedException(NotUsed);

    /// <inheritdoc/>
    public IHttpRequestBuilder WithVersion(Version version) => throw new NotSupportedException(NotUsed);

    /// <inheritdoc/>
    public IHttpRequestBuilder WithVersionPolicy(HttpVersionPolicy policy) => throw new NotSupportedException(NotUsed);

    /// <inheritdoc/>
    public IHttpRequestBuilder WithJsonOptions(JsonSerializerOptions? options) => throw new NotSupportedException(NotUsed);

    /// <inheritdoc/>
    public Task<HttpResponseMessage> SendAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(NotUsed);

    /// <inheritdoc/>
    public Task<HttpResponseMessage> SendAsync(
        HttpCompletionOption completionOption, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(NotUsed);
}
