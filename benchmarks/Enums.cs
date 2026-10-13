using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization;
using BenchmarkDotNet.Attributes;

namespace Snail.Toolkit.HttpBuilder.Extensions.Benchmarks;

/// <summary>
/// A thousand rows whose enum crosses the wire by its <see cref="EnumMemberAttribute"/> name, read and written
/// through <see cref="EnumMemberJsonConverter{TEnum}"/>: once per value of every answer that carries one.
/// </summary>
[MemoryDiagnoser]
public class Enums
{
    private const int Count = 1000;

    private Payment[] _rows = [];
    private byte[] _document = [];

    /// <summary>
    /// Makes the rows, every spelling the converter reads among them, and their JSON; checks each spelling reads as the
    /// member it names and each row is written with its member's wire name.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        string[] spellings = ["ONE_TIME", "recurring", "Unscheduled", "one_time", "RECURRING"];

        _rows = [.. Enumerable.Range(0, Count).Select(index => new Payment(index, (PaymentType)(index % 3)))];
        _document = JsonSerializer.SerializeToUtf8Bytes(
            Enumerable.Range(0, Count).Select(index => new { id = index, type = spellings[index % spellings.Length] }),
            JsonSerializerOptions.Web);

        var read = JsonSerializer.Deserialize<Payment[]>(_document, JsonSerializerOptions.Web)!;

        if (!read.Select(row => row.Type).SequenceEqual(Enumerable.Range(0, Count).Select(index => (PaymentType)(index % 5 % 3))))
            throw new InvalidOperationException("The rows did not read back as the members their spellings name.");

        string[] names = ["ONE_TIME", "RECURRING", "Unscheduled"];
        var written = JsonSerializer.SerializeToUtf8Bytes(_rows, JsonSerializerOptions.Web);
        var expected = JsonSerializer.SerializeToUtf8Bytes(
            _rows.Select(row => new { id = row.Id, type = names[(int)row.Type] }), JsonSerializerOptions.Web);

        if (!written.AsSpan().SequenceEqual(expected))
            throw new InvalidOperationException("The rows were not written with the names their members carry.");
    }

    /// <summary>Reads the thousand rows.</summary>
    [Benchmark]
    public int Read() => JsonSerializer.Deserialize<Payment[]>(_document, JsonSerializerOptions.Web)!.Length;

    /// <summary>Writes the thousand rows.</summary>
    [Benchmark]
    public int Write() => JsonSerializer.SerializeToUtf8Bytes(_rows, JsonSerializerOptions.Web).Length;
}

/// <summary>A payment, the row the benchmark reads and writes.</summary>
public sealed record Payment(int Id, PaymentType Type);

/// <summary>Kinds of payment, with wire names that differ from the member names.</summary>
[JsonConverter(typeof(EnumMemberJsonConverter<PaymentType>))]
public enum PaymentType
{
    /// <summary>Paid once.</summary>
    [EnumMember(Value = "ONE_TIME")]
    OneTime,

    /// <summary>Paid on a schedule.</summary>
    [EnumMember(Value = "RECURRING")]
    Recurring,

    /// <summary>Paid when asked.</summary>
    Unscheduled
}
