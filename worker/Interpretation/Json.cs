using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Eisenberg.Worker.Interpretation;

static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public static string Str(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : node?.ToString() ?? "";

    public static bool Bool(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    public static int Int(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<int>(out var number) ? number : 0;

    public static JsonArray Array(JsonNode? node) => node as JsonArray ?? [];

    public static string Trim(string text, int length = 600) =>
        text.Length <= length ? text : text[..length] + "…";

    // Tool results can wrap the returned value in prose or code fences, so complete JSON values are searched for.
    public static JsonNode? FirstValue(string text, Func<JsonNode?, bool>? accept = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        accept ??= _ => true;
        try
        {
            var whole = JsonNode.Parse(text);
            if (accept(whole)) return whole;
        }
        catch (JsonException) { }
        var bytes = Encoding.UTF8.GetBytes(text);
        for (var start = 0; start < bytes.Length; start++)
        {
            if (bytes[start] != (byte)'{' && bytes[start] != (byte)'[') continue;
            try
            {
                var reader = new Utf8JsonReader(bytes.AsSpan(start));
                reader.Read();
                reader.Skip();
                var node = JsonNode.Parse(bytes.AsSpan(start, (int)reader.BytesConsumed));
                if (accept(node)) return node;
                start += (int)reader.BytesConsumed - 1;
            }
            catch (JsonException) { }
        }
        return null;
    }
}
