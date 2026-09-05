using System.Net.Http.Headers;

namespace Snail.Toolkit.HttpBuilder.Extensions.Tests.Extensions;

/// <summary>Rendering helpers the Moq matchers compare requests with.</summary>
/// <remarks>
/// Content is read synchronously because a Moq matcher is a synchronous predicate; the
/// content under test is always in-memory, so the read completes without blocking.
/// </remarks>
public static class Extensions
{
    /// <summary>Renders a dictionary as one comparable string.</summary>
    public static string AsString<TKey, TValue>(this IDictionary<TKey, TValue> dictionary) =>
        string.Concat(dictionary.Select(pair => $"[{pair.Key}:{pair.Value}]"));

    /// <summary>Renders headers as a dictionary of space-joined values.</summary>
    public static Dictionary<string, string> AsDictionary(this HttpHeaders headers) =>
        headers.ToDictionary(header => header.Key, header => string.Join(' ', header.Value));

    /// <summary>Reads content as text; null when there is none.</summary>
    public static string? AsString(this HttpContent? content) =>
        content?.ReadAsStringAsync().GetAwaiter().GetResult();
}
