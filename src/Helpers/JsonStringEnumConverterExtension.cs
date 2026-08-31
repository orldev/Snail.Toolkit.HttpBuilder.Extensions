using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Snail.Toolkit.HttpBuilder.Extensions.Helpers;

/// <summary>
/// Converts an enum to and from the string an API expects, honouring
/// <see cref="EnumMemberAttribute"/> so wire names can differ from member names.
/// </summary>
/// <typeparam name="TEnum">The enum being converted.</typeparam>
/// <remarks>
/// The built-in <see cref="JsonStringEnumConverter"/> ignores
/// <see cref="EnumMemberAttribute"/>, so it cannot express <c>ONE_TIME</c> for a member
/// called <c>OneTime</c>. Reading accepts either spelling; writing prefers the attribute.
/// </remarks>
/// <example>
/// <code>
/// [JsonConverter(typeof(JsonStringEnumConverterExtension&lt;PaymentType&gt;))]
/// public enum PaymentType
/// {
///     [EnumMember(Value = "ONE_TIME")] OneTime,
///     [EnumMember(Value = "RECURRING")] Recurring
/// }
/// </code>
/// </example>
public class JsonStringEnumConverterExtension<TEnum> : JsonConverter<TEnum> where TEnum : struct, Enum
{
    private readonly Dictionary<TEnum, string> _enumToString = new();
    private readonly Dictionary<string, TEnum> _stringToEnum = new();

    /// <summary>Builds the name maps from the enum's members.</summary>
    public JsonStringEnumConverterExtension()
    {
        var type = typeof(TEnum);

        foreach (var value in Enum.GetValues<TEnum>())
        {
            var name = value.ToString();
            var attribute = type.GetMember(name)[0]
                .GetCustomAttributes(typeof(EnumMemberAttribute), false)
                .Cast<EnumMemberAttribute>()
                .FirstOrDefault();

            // TryAdd: an EnumMember value may collide with another member's name, and a
            // duplicate must not break every request that uses the type.
            _stringToEnum.TryAdd(name, value);

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
    }

    /// <summary>Reads an enum from its string form.</summary>
    /// <returns>
    /// The matching member, or <c>default</c> if unrecognised, so an API adding a value
    /// does not break deserialization of everything else.
    /// </returns>
    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();

        return string.IsNullOrEmpty(value) ? default : _stringToEnum.GetValueOrDefault(value);
    }

    /// <summary>Writes an enum as the string the API expects.</summary>
    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
    {
        // A cast integer or combined flags has no mapping; its own representation beats
        // a KeyNotFoundException mid-serialize.
        writer.WriteStringValue(
            _enumToString.TryGetValue(value, out var name) ? name : value.ToString());
    }
}
