using System.Net;
using System.Net.Http.Json;
using Snail.Toolkit.HttpBuilder.Extensions.Tests.Contracts;
using Snail.Toolkit.HttpBuilder.Extensions.Tests.Extensions;
using Snail.Toolkit.HttpBuilder.Extensions.Tests.HttpEndpoints;

namespace Snail.Toolkit.HttpBuilder.Extensions.Tests;

public class TypedHttpClientBaseTests
{
    private const string BaseAddress = "https://www.example.com";

    private const string Path = "myEndpoint";

    [Theory]
    [InlineData("GET")]
    [InlineData("PUT")]
    [InlineData("POST")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    public async Task HttpMethod_FullHttpRequestMessage_ReturnEqual(string httpMethod)
    {
        HttpMethod method = HttpMethod.Parse(httpMethod);
        Request request = new() { Field = Guid.NewGuid().ToString("N") };
        Dictionary<string, string> headers = new() { ["Authorization"] = "Basic 12345" };
        string expectedBody = await JsonContent.Create(request, mediaType: null, options: null).ReadAsStringAsync();

        RecordingHandler handler = new(Canned.Json(new Response { Field = Guid.NewGuid().ToString("N") }));
        using HttpClient httpClient = new(handler) { BaseAddress = new Uri(BaseAddress) };
        SampleExample client = new(httpClient);

        Response? result = await client.Run(method, Path, request, headers, HttpVersion.Version11, HttpVersionPolicy.RequestVersionOrLower);

        HttpRequestMessage sent = handler.Single;
        Assert.NotNull(result);
        Assert.Equal(method, sent.Method);
        Assert.Equal($"{BaseAddress}/{Path}", sent.RequestUri?.ToString());
        Assert.Equal(HttpVersion.Version11, sent.Version);
        Assert.Equal(HttpVersionPolicy.RequestVersionOrLower, sent.VersionPolicy);
        Assert.Equal(expectedBody, Assert.Single(handler.Bodies));
        Assert.Equal(headers, sent.Headers.AsDictionary());
    }

    [Theory]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    [InlineData("TRACE")]
    [InlineData("CONNECT")]
    public async Task HttpMethod_PartialHttpRequestMessage_ReturnEqual(string httpMethod)
    {
        HttpMethod method = HttpMethod.Parse(httpMethod);

        RecordingHandler handler = new(Canned.Json("string"));
        using HttpClient httpClient = new(handler) { BaseAddress = new Uri(BaseAddress) };
        SampleExample client = new(httpClient);

        using HttpResponseMessage result = await client.Run(method, Path);

        HttpRequestMessage sent = handler.Single;
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal(method, sent.Method);
        Assert.Equal($"{BaseAddress}/{Path}", sent.RequestUri?.ToString());
    }
}
