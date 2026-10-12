using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Snail.Toolkit.HttpBuilder.Extensions.Tests.Contracts;
using Snail.Toolkit.HttpBuilder.Extensions.Tests.Extensions;

namespace Snail.Toolkit.HttpBuilder.Extensions.Tests;

/// <summary>
/// The members that take source-generated metadata: they must put on the wire and read back
/// exactly what the reflection-based ones do with the same options.
/// </summary>
public class TypedJsonTests
{
    private static HttpClient Client(RecordingHandler handler) =>
        new(handler) { BaseAddress = new Uri("https://www.example.com") };

    [Fact]
    public async Task AsJson_WithTypeInfo_SendsWhatTheReflectionBodySends()
    {
        var handler = new RecordingHandler();
        using var client = Client(handler);
        var value = new Request { Field = "value \"quoted\" ü" };

        using var reflected = await new HttpRequestBuilder(client, HttpMethod.Post, "thing")
            .AsJson(value)
            .SendAsync();
        using var generated = await new HttpRequestBuilder(client, HttpMethod.Post, "thing")
            .AsJson(value, TestJson.Default.Request)
            .SendAsync();

        Assert.Equal(handler.Bodies[0], handler.Bodies[1]);
        Assert.Equal(
            handler.Requests[0].Content?.Headers.ContentType,
            handler.Requests[1].Content?.Headers.ContentType);
    }

    [Fact]
    public async Task AsJson_WithTypeInfo_LeavesTheJsonOptionsAlone()
    {
        using var client = Client(new RecordingHandler());
        var builder = new HttpRequestBuilder(client, HttpMethod.Post, "thing").WithJsonOptions(JsonSerializerOptions.Default);

        builder.AsJson(new Request(), TestJson.Default.Request);

        Assert.Same(JsonSerializerOptions.Default, builder.JsonOptions);
    }

    [Theory]
    [InlineData("""{"field":"one"}""")]
    [InlineData("")]
    public async Task SendAsync_WithTypeInfo_ReadsWhatTheReflectionTerminalReads(string body)
    {
        using var client = Client(new RecordingHandler(Canned.Text(body, HttpStatusCode.OK)));

        var reflected = await new HttpRequestBuilder(client, HttpMethod.Get, "thing").SendAsync<Response>();
        var generated = await new HttpRequestBuilder(client, HttpMethod.Get, "thing").SendAsync(TestJson.Default.Response);

        Assert.Equal(reflected?.Field, generated?.Field);
    }

    [Fact]
    public async Task SendAsNdjsonAsync_WithTypeInfo_ReadsWhatTheReflectionTerminalReads()
    {
        using var client = Client(new RecordingHandler(Canned.Text(
            """
            {"field":"one"}

            {"field":"two"}
            null
            {"field":"three"}
            """,
            HttpStatusCode.OK)));

        var reflected = await new HttpRequestBuilder(client, HttpMethod.Get, "thing").SendAsNdjsonAsync<Response>().ToListAsync();
        var generated = await new HttpRequestBuilder(client, HttpMethod.Get, "thing").SendAsNdjsonAsync(TestJson.Default.Response).ToListAsync();

        Assert.Equal(["one", "two", "three"], generated.Select(chunk => chunk.Field));
        Assert.Equal(reflected.Select(chunk => chunk.Field), generated.Select(chunk => chunk.Field));
    }

    [Fact]
    public async Task SendAsJsonStreamAsync_WithTypeInfo_ReadsWhatTheReflectionTerminalReads()
    {
        using var client = Client(new RecordingHandler(Canned.Text(
            """[{"field":"one"},null,{"field":"two"}]""", HttpStatusCode.OK)));

        var reflected = await new HttpRequestBuilder(client, HttpMethod.Get, "thing").SendAsJsonStreamAsync<Response>().ToListAsync();
        var generated = await new HttpRequestBuilder(client, HttpMethod.Get, "thing").SendAsJsonStreamAsync(TestJson.Default.Response).ToListAsync();

        Assert.Equal(["one", "two"], generated.Select(element => element.Field));
        Assert.Equal(reflected.Select(element => element.Field), generated.Select(element => element.Field));
    }

    [Fact]
    public async Task SendAsSseAsync_WithTypeInfo_ReadsWhatTheReflectionTerminalReads()
    {
        const string stream = """
            id: 7
            event: delta
            data: {"field":"one"}

            : comment
            data: {"field":
            data: "two"}

            data: [DONE]

            data: {"field":"never"}

            """;
        using var client = Client(new RecordingHandler(Canned.Text(stream, HttpStatusCode.OK)));

        var reflected = await new HttpRequestBuilder(client, HttpMethod.Get, "thing").SendAsSseEventsAsync<Response>().ToListAsync();
        var generated = await new HttpRequestBuilder(client, HttpMethod.Get, "thing").SendAsSseEventsAsync(TestJson.Default.Response).ToListAsync();
        var data = await new HttpRequestBuilder(client, HttpMethod.Get, "thing").SendAsSseAsync(TestJson.Default.Response).ToListAsync();

        Assert.Equal(["one", "two"], data.Select(chunk => chunk.Field));
        Assert.Equal(
            reflected.Select(received => (received.Event, received.Id, received.Data.Field)),
            generated.Select(received => (received.Event, received.Id, received.Data.Field)));
    }

    [Fact]
    public async Task Terminals_WithoutTypeInfo_ThrowBeforeSending()
    {
        var handler = new RecordingHandler();
        using var client = Client(handler);

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            new HttpRequestBuilder(client, HttpMethod.Get, "thing").SendAsync<Response>(typeInfo: null!));
        Assert.Throws<ArgumentNullException>(() =>
            new HttpRequestBuilder(client, HttpMethod.Get, "thing").SendAsNdjsonAsync<Response>(typeInfo: null!));
        Assert.Throws<ArgumentNullException>(() =>
            new HttpRequestBuilder(client, HttpMethod.Get, "thing").SendAsSseEventsAsync<Response>(typeInfo: null!));
        Assert.Empty(handler.Requests);
    }
}

/// <summary>Source-generated metadata for the test contracts, with the builder's default web options.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(Request))]
[JsonSerializable(typeof(Response))]
internal sealed partial class TestJson : JsonSerializerContext;
