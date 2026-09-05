## Snail.Toolkit.HttpBuilder.Extensions

Extension for the framework `HttpClient`. Derive a typed client from
`TypedHttpClientBase`, start a request with a verb, configure it fluently, send it.

```csharp
public class Request;

public class Response;

public class SampleExample(HttpClient client) : TypedHttpClientBase(client)
{
    public Task<Response?> Run(Request request, CancellationToken ct = default) =>
        Post("v1/oauth2/token")
            .AsJson(request)
            .Header("Authorization", "Basic 12345")
            .SendAsync<Response>(ct);
}
```

Register it as a typed client so it picks up the base address and handler pipeline:

```csharp
services.AddHttpClient<SampleExample>(c => c.BaseAddress = new Uri("https://api.example.com"));
```

#### Each request is independent

Every verb returns a **new** builder, so nothing set on one call can reach the next
one. A client is safe to reuse and safe to call concurrently:

```csharp
await Post("v1/things").AsJson(body).Header("X-Trace", id).SendAsync<Thing>(ct);
await Get("v1/things/1").SendAsync<Thing>(ct);   // no body, no X-Trace
```

#### Building the request

| Method | |
| --- | --- |
| `AsJson(value, options?)` | JSON body, serialized at send time; `options`, when given, become the builder's JSON options |
| `AsString(value, encoding?, mediaType?)` | UTF-8 and `text/plain` by default |
| `AsForm(fields)` | `application/x-www-form-urlencoded` |
| `Content(httpContent)` | a body you prepared yourself |
| `Header(name, value)` / `Headers(pairs)` | additive; content headers are routed to the body |
| `Accept(mediaType)` | shorthand for the `Accept` header |
| `Query(name, value)` / `Query(pairs)` | escaped and appended; **null values are skipped** |
| `WithVersion` / `WithVersionPolicy` | HTTP version; defaults to the client's `DefaultRequestVersion` and policy |
| `WithJsonOptions(options)` | serializer options for the body and the response; the options given last win |

#### Sending

| Method | |
| --- | --- |
| `SendAsync(ct)` | the raw `HttpResponseMessage`, whatever its status — **you dispose it** |
| `SendCheckedAsync(option, ct)` | the raw response with the status verified — a failure throws |
| `SendAsync<T>(ct)` | deserializes JSON; `default` when the response has no content |
| `SendAsStringAsync(ct)` | the body as text |
| `SendAsNdjsonAsync<T>(ct)` | streams newline-delimited JSON, one `T` per line as it arrives |
| `SendAsJsonStreamAsync<T>(ct)` | streams the elements of a JSON array as they arrive |
| `SendAsSseAsync<T>(ct)` | streams the `data` payloads of server-sent events as they arrive |
| `SendAsSseEventsAsync<T>(ct)` | streams whole server-sent events, keeping `event` names and ids |

`IHttpRequestBuilder` itself carries only the configuration and the raw and checked
sends and is **frozen**; the typed and streaming terminals are extension methods
(`HttpRequestTerminals`), so a new response format never breaks an implementor of the
interface.

A builder is **single-use**: sending disposes the request content, so a second send on
the same instance throws `InvalidOperationException`. Start the next call from a verb
on the client.

The typed overloads throw `HttpBuilderException` on a failure status. It carries
the method, URI, status and — the useful part — the **response body**, which is where
APIs explain what was wrong and which is otherwise lost. The body is read capped (4 KB),
so an oversized error response cannot exhaust memory:

```csharp
catch (HttpBuilderException ex)
{
    logger.LogError("{Status}: {Body}", ex.StatusCode, ex.Body);
}
```

It derives from `HttpRequestException`, so existing handlers still catch it.

#### Streaming

All streaming terminals send with `ResponseHeadersRead`, so the first chunk is
yielded while the server is still generating the rest — which is what an LLM-style
endpoint needs:

```csharp
public class ChatClient(HttpClient client) : TypedHttpClientBase(client)
{
    // Ollama-style NDJSON: {"done":false,...}\n{"done":false,...}\n{"done":true,...}
    public IAsyncEnumerable<ChatChunk> StreamAsync(ChatRequest request, CancellationToken ct = default) =>
        Post("/api/chat").AsJson(request).SendAsNdjsonAsync<ChatChunk>(ct);
}

await foreach (var chunk in chat.StreamAsync(request, ct))
{
    Console.Write(chunk.Message.Content);
    if (chunk.Done) break;   // domain-level completion stays at the call site
}
```

`SendAsNdjsonAsync<T>` reads line-delimited JSON, skipping blank lines and JSON
`null`s. `SendAsJsonStreamAsync<T>` reads the elements of one JSON array
(`[{...},{...},...]`) without waiting for the closing bracket. `SendAsSseAsync<T>`
reads a `text/event-stream` response, yielding the `data` payload of each event:
comment lines and other fields are skipped, multi-line data is joined with newlines,
and an OpenAI-style `data: [DONE]` sentinel ends the stream:

```csharp
public class ChatClient(HttpClient client) : TypedHttpClientBase(client)
{
    // OpenAI-style SSE: data: {...}\n\n data: {...}\n\n data: [DONE]
    public IAsyncEnumerable<ChatChunk> StreamAsync(ChatRequest request, CancellationToken ct = default) =>
        Post("v1/chat/completions")
            .AsJson(request)
            .Accept("text/event-stream")
            .SendAsSseAsync<ChatChunk>(ct);
}
```

When the event names matter — Anthropic-style streams discriminate on `event:` —
`SendAsSseEventsAsync<T>` yields whole events instead, keeping each event's name and
the stream's last `id` alongside the parsed payload.

All of them honour `WithJsonOptions` and default to web-style (camelCase,
case-insensitive) options, the same as `SendAsync<T>`, and all throw
`HttpBuilderException` with the response body on a failure status.

#### Paths

A path is resolved against the client's `BaseAddress`, keeping any path the base
address carries:

| `BaseAddress` | path | request |
| --- | --- | --- |
| `https://host` | `v1/things` | `https://host/v1/things` |
| `https://host/crm/v8/` | `Keynex` | `https://host/crm/v8/Keynex` |
| `https://host/crm/v8/` | `/ping` | `https://host/ping` |
| anything | `https://other/v2/x` | `https://other/v2/x` |

Google-style paths containing a colon (`v1/places:autocomplete`) are handled — the
segment before the colon is not mistaken for a URI scheme.

#### Enum wire names

`EnumMemberJsonConverter` maps enum members to the strings an API expects,
which the built-in converter cannot do because it ignores `EnumMember`.

```csharp
[JsonConverter(typeof(EnumMemberJsonConverter<PaymentType>))]
public enum PaymentType
{
    [EnumMember(Value = "ONE_TIME")]
    OneTime,
    [EnumMember(Value = "RECURRING")]
    Recurring,
    [EnumMember(Value = "UNSCHEDULED")]
    Unscheduled
}
```

```json lines
{
  "value_enum": "ONE_TIME"
}
```

Both the member name and the `EnumMember` value are accepted when reading, ignoring
case, and a numeric wire value maps onto the defined member it names. Anything
unrecognised reads as `default` rather than throwing, so an API adding a value — or
switching its serializer to numbers — does not break deserialization of everything
else.

## License

Snail.Toolkit.HttpBuilder.Extensions is a free and open source project, released under the permissible [MIT license](LICENSE).