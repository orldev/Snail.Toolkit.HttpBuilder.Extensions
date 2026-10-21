namespace Snail.Toolkit.HttpBuilder.Extensions;

public static partial class HttpRequestTerminals
{
    /// <summary>Sends the request and returns a successful response as text.</summary>
    /// <exception cref="HttpBuilderException">The status was not a success.</exception>
    public static async Task<string> SendAsStringAsync(
        this IHttpRequestBuilder builder, CancellationToken cancellationToken = default)
    {
        using var response = await builder
            .SendCheckedAsync(HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);

        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }
}
