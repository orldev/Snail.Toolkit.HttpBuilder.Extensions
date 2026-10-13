using System.Net;
using System.Runtime.Serialization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Snail.Toolkit.HttpBuilder.Extensions;

using var client = new HttpClient(new Answers()) { BaseAddress = new Uri("http://probe.local/") };

var echoed = await new HttpRequestBuilder(client, HttpMethod.Post, "echo")
    .AsJson(new Order(7, Size.ExtraLarge), ProbeJson.Default.Order)
    .SendAsync(ProbeJson.Default.Order, CancellationToken.None);
var streamed = await new HttpRequestBuilder(client, HttpMethod.Post, "echo")
    .AsJson(new Order(8, Size.Small), ProbeJson.Default.Order)
    .SendStreamedAsync(ProbeJson.Default.Order, CancellationToken.None);
var lines = await new HttpRequestBuilder(client, HttpMethod.Get, "lines")
    .SendAsNdjsonAsync(ProbeJson.Default.Order, CancellationToken.None)
    .ToListAsync();
var elements = await new HttpRequestBuilder(client, HttpMethod.Get, "array")
    .SendAsJsonStreamAsync(ProbeJson.Default.Order, CancellationToken.None)
    .ToListAsync();
var events = await new HttpRequestBuilder(client, HttpMethod.Get, "events")
    .SendAsSseEventsAsync(ProbeJson.Default.Order, CancellationToken.None)
    .ToListAsync();
var empty = await new HttpRequestBuilder(client, HttpMethod.Get, "empty")
    .SendAsync(ProbeJson.Default.Order, CancellationToken.None);

var isEchoed = echoed is { Id: 7, Size: Size.ExtraLarge }
    && streamed is { Id: 8, Size: Size.Small }
    && Answers.Echoed == """{"id":8,"size":"S"}""";
var isStreamed = lines.Count == 2 && lines[1] is { Id: 2, Size: Size.Small }
    && elements.Count == 2 && elements[0] is { Id: 1, Size: Size.ExtraLarge };
var isEvented = events.Count == 1 && events[0] is { Event: "order", Id: "9", Data.Size: Size.Small };

Console.WriteLine($"echoed {isEchoed}, streamed {isStreamed}, events {isEvented}, empty {empty is null}");

return isEchoed && isStreamed && isEvented && empty is null ? 0 : 1;

/// <summary>An order with an enum written by its EnumMember name.</summary>
internal sealed record Order(int Id, Size Size);

/// <summary>A size with wire names that differ from the member names.</summary>
[JsonConverter(typeof(EnumMemberJsonConverter<Size>))]
internal enum Size
{
    [EnumMember(Value = "S")]
    Small,

    [EnumMember(Value = "XL")]
    ExtraLarge
}

/// <summary>The probe's source-generated metadata, with the web defaults the builder uses.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(Order))]
internal sealed partial class ProbeJson : JsonSerializerContext;

/// <summary>Answers by path from memory: the body echoed, lines, an array, events, nothing.</summary>
internal sealed class Answers : HttpMessageHandler
{
    public static string? Echoed { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var sent = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

        if (request.RequestUri!.AbsolutePath == "/echo")
        {
            Echoed = sent;
        }

        var body = request.RequestUri.AbsolutePath switch
        {
            "/echo" => sent ?? string.Empty,
            "/lines" => "{\"id\":1,\"size\":\"xl\"}\n\nnull\n{\"id\":2,\"size\":\"Small\"}\n",
            "/array" => "[{\"id\":1,\"size\":\"XL\"},null,{\"id\":3,\"size\":0}]",
            "/events" => "event: order\nid: 9\ndata: {\"id\":9,\"size\":\"S\"}\n\ndata: [DONE]\n\n",
            _ => string.Empty
        };

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
            RequestMessage = request
        };
    }
}
