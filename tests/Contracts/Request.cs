using System.Text.Json.Serialization;

namespace Snail.Toolkit.HttpBuilder.Extensions.Tests.Contracts;

public class Request
{
    [JsonPropertyName("field")]
    public string? Field { get; set; }
};