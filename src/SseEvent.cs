namespace Snail.Toolkit.HttpBuilder.Extensions;

/// <summary>One server-sent event with its payload parsed.</summary>
/// <typeparam name="TValue">The type the <c>data</c> payload is read as.</typeparam>
/// <param name="Event">The <c>event</c> field, or null when the server named none.</param>
/// <param name="Id">The last <c>id</c> the stream declared, or null when it never did.</param>
/// <param name="Data">The parsed <c>data</c> payload.</param>
public sealed record SseEvent<TValue>(string? Event, string? Id, TValue Data);
