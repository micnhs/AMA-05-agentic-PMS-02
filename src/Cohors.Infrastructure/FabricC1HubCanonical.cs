using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cohors.Domain;

namespace Cohors.Infrastructure;

internal static class FabricC1HubCanonical
{
    // The hub owner uses Python json.dumps(sort_keys=True, separators=(",", ":"), ensure_ascii=True).
    // Keep this wire digest separate from Cohors' historical canonical payload hashes.
    public static string Hash(JsonNode? value)
    {
        var text = new StringBuilder();
        Write(text, value);
        return Canonical.HashText(text.ToString());
    }

    private static void Write(StringBuilder text, JsonNode? value)
    {
        switch (value)
        {
            case null: text.Append("null"); break;
            case JsonObject obj:
                text.Append('{');
                var separator = "";
                foreach (var field in obj.OrderBy(field => field.Key, Comparer<string>.Create(CompareKeys)))
                {
                    text.Append(separator);
                    String(text, field.Key);
                    text.Append(':');
                    Write(text, field.Value);
                    separator = ",";
                }
                text.Append('}');
                break;
            case JsonArray array:
                text.Append('[');
                for (var index = 0; index < array.Count; index++)
                {
                    if (index != 0) text.Append(',');
                    Write(text, array[index]);
                }
                text.Append(']');
                break;
            default:
                if (value.GetValueKind() == JsonValueKind.String) String(text, value.GetValue<string>());
                else if (value.GetValueKind() == JsonValueKind.Number) text.Append(Number(value.ToJsonString()));
                else if (value.GetValueKind() is JsonValueKind.True or JsonValueKind.False) text.Append(value.ToJsonString());
                else throw new DomainException("c1_hub_hash_shape", "Unsupported hub wire value.", 409);
                break;
        }
    }

    private static void String(StringBuilder text, string value)
    {
        text.Append('"');
        foreach (var character in value)
            switch (character)
            {
                case '"': text.Append("\\\""); break;
                case '\\': text.Append("\\\\"); break;
                case '\b': text.Append("\\b"); break;
                case '\f': text.Append("\\f"); break;
                case '\n': text.Append("\\n"); break;
                case '\r': text.Append("\\r"); break;
                case '\t': text.Append("\\t"); break;
                default:
                    if (character < 32 || character > 126) text.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    else text.Append(character);
                    break;
            }
        text.Append('"');
    }

    private static int CompareKeys(string left, string right)
    {
        static int Next(string value, ref int index)
        {
            var first = value[index++];
            return char.IsHighSurrogate(first) && index < value.Length && char.IsLowSurrogate(value[index])
                ? char.ConvertToUtf32(first, value[index++]) : first;
        }
        var a = 0;
        var b = 0;
        while (a < left.Length && b < right.Length)
        {
            var comparison = Next(left, ref a).CompareTo(Next(right, ref b));
            if (comparison != 0) return comparison;
        }
        return (a < left.Length ? 1 : 0) - (b < right.Length ? 1 : 0);
    }

    private static string Number(string raw)
    {
        if (raw.IndexOfAny(['.', 'e', 'E']) < 0) return BigInteger.Parse(raw, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
        var number = double.Parse(raw, CultureInfo.InvariantCulture);
        Guard.Against(!double.IsFinite(number), "c1_hub_hash_shape", "Non-finite ARM numbers cannot enter a hub receipt.", 409);
        var sign = double.IsNegative(number) ? "-" : "";
        var shortest = Math.Abs(number).ToString("R", CultureInfo.InvariantCulture).ToLowerInvariant();
        var parts = shortest.Split('e');
        var coefficient = parts[0];
        var exponent = parts.Length == 2 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 0;
        var point = coefficient.IndexOf('.');
        if (point < 0) point = coefficient.Length;
        var digits = coefficient.Replace(".", "", StringComparison.Ordinal);
        var leading = digits.Length - digits.TrimStart('0').Length;
        if (leading == digits.Length) return sign + "0.0";
        exponent += point - leading - 1;
        digits = digits[leading..].TrimEnd('0');
        if (exponent is < -4 or >= 16)
            return sign + digits[0] + (digits.Length == 1 ? "" : "." + digits[1..]) +
                "e" + (exponent < 0 ? "-" : "+") + Math.Abs(exponent).ToString("D2", CultureInfo.InvariantCulture);
        var places = exponent + 1;
        return sign + (places <= 0 ? "0." + new string('0', -places) + digits :
            places >= digits.Length ? digits + new string('0', places - digits.Length) + ".0" :
            digits[..places] + "." + digits[places..]);
    }
}
