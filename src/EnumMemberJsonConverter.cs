using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Snail.Toolkit.HttpBuilder.Extensions;

/// <summary>
/// Converts an enum to and from the string an API expects, honouring
/// <see cref="EnumMemberAttribute"/> so wire names can differ from member names.
/// </summary>
/// <typeparam name="TEnum">The enum being converted.</typeparam>
/// <remarks>
/// The built-in <see cref="JsonStringEnumConverter"/> ignores
/// <see cref="EnumMemberAttribute"/>, so it cannot express <c>ONE_TIME</c> for a member
/// called <c>OneTime</c>. Reading accepts either spelling, ignores case, and takes a
/// number too; writing prefers the attribute. The enum's public fields — its members — are
/// read once, and kept by the trimmer for it.
/// </remarks>
/// <example>
/// <code>
/// [JsonConverter(typeof(EnumMemberJsonConverter&lt;PaymentType&gt;))]
/// public enum PaymentType
/// {
///     [EnumMember(Value = "ONE_TIME")] OneTime,
///     [EnumMember(Value = "RECURRING")] Recurring
/// }
/// </code>
/// </example>
public sealed class EnumMemberJsonConverter<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)] TEnum>
    : JsonConverter<TEnum> where TEnum : struct, Enum
{
    private readonly Dictionary<TEnum, string> _enumToString = [];
    private readonly Dictionary<TEnum, JsonEncodedText> _enumToEncoded = [];
    private readonly Dictionary<string, TEnum> _stringToEnum = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TEnum>.AlternateLookup<ReadOnlySpan<char>> _spanToEnum;
    private readonly Dictionary<long, TEnum> _numberToEnum = [];

    /// <summary>The longest wire value, in UTF-8 bytes, read on the stack rather than into a string.</summary>
    private const int StackNameLimit = 128;

    /// <summary>Builds the name maps from the enum's members.</summary>
    /// <remarks>
    /// Maps are filled with <c>TryAdd</c>: an <see cref="EnumMemberAttribute"/> value may
    /// collide with another member's name, and a duplicate must not break every request
    /// that uses the type. The first mapping registered wins.
    /// </remarks>
    public EnumMemberJsonConverter()
    {
        var type = typeof(TEnum);

        foreach (var value in Enum.GetValues<TEnum>())
        {
            var name = value.ToString();
            var attribute = type
                .GetField(name, BindingFlags.Public | BindingFlags.Static)?
                .GetCustomAttribute<EnumMemberAttribute>(inherit: false);

            _stringToEnum.TryAdd(name, value);

            if (TryNumberOf(value, out var number))
            {
                _numberToEnum.TryAdd(number, value);
            }

            if (attribute?.Value is not null)
            {
                _enumToString.TryAdd(value, attribute.Value);
                _stringToEnum.TryAdd(attribute.Value, value);
            }
            else
            {
                _enumToString.TryAdd(value, name);
            }
        }

        foreach (var (value, name) in _enumToString)
        {
            _enumToEncoded.Add(value, JsonEncodedText.Encode(name));
        }

        _spanToEnum = _stringToEnum.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    /// <summary>Reads an enum from its string or numeric form, ignoring case.</summary>
    /// <returns>
    /// The matching member, or <c>default</c> if unrecognised, so an API adding a value —
    /// or switching its serializer to numbers — does not break deserialization of
    /// everything else.
    /// </returns>
    /// <remarks>
    /// A string of up to <see cref="StackNameLimit"/> bytes is unescaped onto the stack and
    /// looked up from there, so reading a value costs no string.
    /// </remarks>
    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            return reader.TryGetInt64(out var number) ? _numberToEnum.GetValueOrDefault(number) : default;
        }

        var length = reader.HasValueSequence ? reader.ValueSequence.Length : reader.ValueSpan.Length;

        if (reader.TokenType == JsonTokenType.String && length <= StackNameLimit)
        {
            Span<char> name = stackalloc char[StackNameLimit];
            var written = reader.CopyString(name);

            return written > 0 && _spanToEnum.TryGetValue(name[..written], out var member) ? member : default;
        }

        var value = reader.GetString();

        return string.IsNullOrEmpty(value) ? default : _stringToEnum.GetValueOrDefault(value);
    }

    /// <summary>The number a member stands for, when a JSON number read as <see cref="long"/> can name it.</summary>
    /// <remarks>
    /// Numbers are matched against the members' own values, never cast to the enum: <see cref="Enum.ToObject(Type, long)"/>
    /// truncates to the underlying type, so 258 on a byte enum read as the member 2. A <see cref="ulong"/> member
    /// above <see cref="long.MaxValue"/> has no such number and reads as <c>default</c>, as before.
    /// </remarks>
    private static bool TryNumberOf(TEnum value, out long number)
    {
        if (Type.GetTypeCode(typeof(TEnum)) != TypeCode.UInt64)
        {
            number = Convert.ToInt64(value, CultureInfo.InvariantCulture);

            return true;
        }

        var unsigned = Convert.ToUInt64(value, CultureInfo.InvariantCulture);
        number = unchecked((long)unsigned);

        return unsigned <= long.MaxValue;
    }

    /// <summary>Writes an enum as the string the API expects.</summary>
    /// <remarks>
    /// A cast integer or combined flags has no mapping; its own representation beats a
    /// <see cref="KeyNotFoundException"/> mid-serialize. A writer with the default encoder
    /// gets the name encoded once, up front, so writing it neither transcodes nor escapes;
    /// one with an encoder of its own escapes the name as it always did.
    /// </remarks>
    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
    {
        if (writer.Options.Encoder is null && _enumToEncoded.TryGetValue(value, out var encoded))
        {
            writer.WriteStringValue(encoded);

            return;
        }

        writer.WriteStringValue(
            _enumToString.TryGetValue(value, out var name) ? name : value.ToString());
    }
}
