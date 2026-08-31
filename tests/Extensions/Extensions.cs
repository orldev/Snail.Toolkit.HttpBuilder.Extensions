using System.Net.Http.Headers;
using System.Text;

namespace Snail.Toolkit.HttpBuilder.Extensions.Tests.Extensions;

public static class Extensions
{
    public static string AsString<TKey,TValue>(this IDictionary<TKey,TValue> dict)
    {
        var sb = new StringBuilder();
        foreach (var (key, value) in dict) {
            sb.Append($"[{key}:{value}]");
        }
        return sb.ToString();
    }
    
    public static Dictionary<string, string> AsDictionary(this HttpHeaders headers)
    {
        var dict = new Dictionary<string, string>();

        foreach (var (key, value) in headers.ToList())
        {
            var header = value.Aggregate(string.Empty, (current, v) => current + v + " ");
                
            // Trim the trailing space and add item to the dictionary
            header = header.TrimEnd(" ".ToCharArray());
            dict.Add(key, header);
        }

        return dict;
    }
    
    public static string? AsString(this HttpContent? content)
    {
        return content?.ReadAsStringAsync().Result;
    }
}