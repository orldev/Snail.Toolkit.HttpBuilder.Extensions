using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Snail.Toolkit.HttpBuilder.Extensions.Tests;

/// <summary>
/// Covers the enum converter: wire names come from <see cref="EnumMemberAttribute"/>,
/// reading accepts either spelling, and unknown or unmapped values degrade rather
/// than throwing mid-request.
/// </summary>
public class EnumMemberJsonConverterTests
{
    [JsonConverter(typeof(EnumMemberJsonConverter<Payment>))]
    public enum Payment
    {
        [EnumMember(Value = "ONE_TIME")] OneTime,
        [EnumMember(Value = "RECURRING")] Recurring,
        Unlabelled
    }

    [JsonConverter(typeof(EnumMemberJsonConverter<Colliding>))]
    public enum Colliding
    {
        [EnumMember(Value = "Second")] First,
        Second
    }

    [Fact]
    public void Write_PrefersTheEnumMemberValue() =>
        Assert.Equal("\"ONE_TIME\"", JsonSerializer.Serialize(Payment.OneTime));

    [Fact]
    public void Write_FallsBackToTheMemberName() =>
        Assert.Equal("\"Unlabelled\"", JsonSerializer.Serialize(Payment.Unlabelled));

    /// <summary>
    /// A cast integer has no mapping; its own representation beats an exception in the
    /// middle of serializing a request.
    /// </summary>
    [Fact]
    public void Write_UnmappedValueUsesItsOwnRepresentation() =>
        Assert.Equal("\"7\"", JsonSerializer.Serialize((Payment)7));

    [Fact]
    public void Read_AcceptsTheWireName() =>
        Assert.Equal(Payment.Recurring, JsonSerializer.Deserialize<Payment>("\"RECURRING\""));

    [Fact]
    public void Read_AcceptsTheMemberName() =>
        Assert.Equal(Payment.Recurring, JsonSerializer.Deserialize<Payment>("\"Recurring\""));

    /// <summary>
    /// An API adding a value must not break deserialization of everything else, so an
    /// unknown string degrades to the default member.
    /// </summary>
    [Fact]
    public void Read_UnknownValueBecomesDefault() =>
        Assert.Equal(default, JsonSerializer.Deserialize<Payment>("\"CRYPTO\""));

    [Fact]
    public void Read_IgnoresCase() =>
        Assert.Equal(Payment.Recurring, JsonSerializer.Deserialize<Payment>("\"recurring\""));

    /// <summary>
    /// An API switching its serializer to numbers must degrade like an unknown string,
    /// not explode mid-deserialize.
    /// </summary>
    [Fact]
    public void Read_AcceptsADefinedNumber() =>
        Assert.Equal(Payment.Recurring, JsonSerializer.Deserialize<Payment>("1"));

    [Fact]
    public void Read_UnknownNumberBecomesDefault() =>
        Assert.Equal(default, JsonSerializer.Deserialize<Payment>("42"));

    [Fact]
    public void Read_EveryLabelledMemberRoundTrips()
    {
        foreach (var value in Enum.GetValues<Payment>())
        {
            Assert.Equal(value, JsonSerializer.Deserialize<Payment>(JsonSerializer.Serialize(value)));
        }
    }

    /// <summary>
    /// An EnumMember value colliding with another member's name keeps the first mapping
    /// instead of throwing in the converter's constructor and breaking every request
    /// that uses the type.
    /// </summary>
    [Fact]
    public void Read_CollidingWireNameKeepsTheFirstMapping() =>
        Assert.Equal(Colliding.First, JsonSerializer.Deserialize<Colliding>("\"Second\""));
}
