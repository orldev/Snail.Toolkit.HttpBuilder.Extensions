using System.Net;
using System.Net.Http.Json;
using Moq;
using Moq.Protected;
using Snail.Toolkit.HttpBuilder.Extensions.Tests.Contracts;
using Snail.Toolkit.HttpBuilder.Extensions.Tests.Extensions;
using Snail.Toolkit.HttpBuilder.Extensions.Tests.HttpEndpoints;

namespace Snail.Toolkit.HttpBuilder.Extensions.Tests;

public class TypedHttpClientBaseTests
{
    [Theory]
    [InlineData("GET")]
    [InlineData("PUT")]
    [InlineData("POST")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    public async Task HttpMethod_FullHttpRequestMessage_ReturnEqual(string httpMethod)
    {
        // ARRANGE
        var fakeBaseAddress = "https://www.example.com";
        var path = "myEndpoint";
        
        var requestMethod = HttpMethod.Parse(httpMethod);
        var requestUrl = $"{fakeBaseAddress}/{path}";
        var requestVersion = HttpVersion.Version11;
        var requestVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        var request = new Request { Field = Guid.NewGuid().ToString("N") };
        var requestContent = JsonContent.Create(request, mediaType: null, options: null);
        var requestHeaders = new Dictionary<string, string>
        {
            {"Authorization", "Basic 12345"}
        };
        
        var response = new Response { Field = Guid.NewGuid().ToString("N")};
        
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock
            .SetupSendAsync(requestMethod, 
                requestUrl,
                requestContent,
                requestHeaders,
                requestVersion, 
                requestVersionPolicy)
            .ReturnsHttpResponseAsync(response, HttpStatusCode.OK);
        
        var httpClient = new HttpClient(handlerMock.Object)
        {
            BaseAddress = new Uri(fakeBaseAddress)
        };
        
        var client = new SampleExample(httpClient);
        
        // ACT
        var result = await client.Run(requestMethod, path, request, requestHeaders, requestVersion, requestVersionPolicy);
        
        // ASSERT
        Assert.NotNull(result);
        handlerMock.Protected().Verify(
            "SendAsync",
            Times.Exactly(1),
            ItExpr.Is<HttpRequestMessage>(r =>
                r.Method == requestMethod &&
                r.RequestUri != null &&
                r.RequestUri.ToString() == requestUrl &&
                r.Version == requestVersion &&
                r.VersionPolicy == requestVersionPolicy &&
                r.Content.AsString() == requestContent.AsString() &&
                r.Headers.AsDictionary().AsString() == requestHeaders.AsString()
            ),
            ItExpr.IsAny<CancellationToken>());
    }
    
    [Theory]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    [InlineData("TRACE")]
    [InlineData("CONNECT")]
    public async Task HttpMethod_PartialHttpRequestMessage_ReturnEqual(string httpMethod)
    {
        // ARRANGE
        var fakeBaseAddress = "https://www.example.com";
        var path = "myEndpoint";
        
        var requestMethod = HttpMethod.Parse(httpMethod);
        var requestUrl = $"{fakeBaseAddress}/{path}";
        var response = "string";
        
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock
            .SetupSendAsync(requestMethod, 
                requestUrl)
            .ReturnsHttpResponseAsync(response, HttpStatusCode.OK);
        
        var httpClient = new HttpClient(handlerMock.Object)
        {
            BaseAddress = new Uri(fakeBaseAddress)
        };
        
        var client = new SampleExample(httpClient);
        
        // ACT
        var result = await client.Run(requestMethod, path);
        
        // ASSERT
        Assert.NotNull(result);
        handlerMock.Protected().Verify(
            "SendAsync",
            Times.Exactly(1),
            ItExpr.Is<HttpRequestMessage>(r =>
                r.Method == requestMethod &&
                r.RequestUri != null &&
                r.RequestUri.ToString() == requestUrl
            ),
            ItExpr.IsAny<CancellationToken>());
    }
    
}