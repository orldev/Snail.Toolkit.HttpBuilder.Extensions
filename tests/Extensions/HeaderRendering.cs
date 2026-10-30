using System.Net.Http.Headers;

namespace Snail.Toolkit.HttpBuilder.Extensions.Tests.Extensions;

/// <summary>Renders headers in a shape an assertion compares.</summary>
public static class HeaderRendering
{
    /// <summary>Renders headers as a dictionary of space-joined values.</summary>
    public static Dictionary<string, string> AsDictionary(this HttpHeaders headers) =>
        headers.ToDictionary(header => header.Key, header => string.Join(' ', header.Value));
}
