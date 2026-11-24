using System.Text.Json;
using System.Text.RegularExpressions;

namespace Eisenberg.Data;

public sealed record LocalSource(string FileName, int? Page, string Location);

public sealed record LocalRecord(
    string TypeName,
    string Label,
    IReadOnlyDictionary<string, string> Values,
    IReadOnlyDictionary<string, LocalSource> Sources);

public static class LocalRecords
{
    public static IReadOnlyList<LocalRecord> FromReadings(
        string modelJson,
        IEnumerable<(string Id, bool Complete, string ChoicesJson)> lists,
        IEnumerable<(string FileName, string Reading)> readings)
    {
        var documents = readings
            .Select(reading => (reading.FileName, Text: Plain(reading.Reading)))
            .Where(reading => reading.Text.Length > 0)
            .ToList();
        if (documents.Count == 0) return [];
        var runtime = FormRuntime.Parse(modelJson, lists);
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(modelJson) ? """{"types":[]}""" : modelJson);
        if (!document.RootElement.TryGetProperty("types", out var types) || types.ValueKind != JsonValueKind.Array)
            return [];
        var records = new List<LocalRecord>();
        foreach (var type in types.EnumerateArray())
        {
            var name = type.TryGetProperty("name", out var typeName) && typeName.ValueKind == JsonValueKind.String ? typeName.GetString() ?? "" : "";
            if (name.Length == 0) continue;
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var sources = new Dictionary<string, LocalSource>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in runtime.PropertiesOf(name, null))
            {
                if (property.Ignored) continue;
                var found = property.ChoiceListComplete && property.Choices.Count > 0
                    ? Choice(property, documents)
                    : Labeled(property, documents);
                if (found is null) continue;
                values[property.Name] = found.Value.Value;
                sources[property.Name] = found.Value.Source;
            }
            if (values.Count == 0) continue;
            var strings = values.ToDictionary(pair => pair.Key, pair => (string?)pair.Value, StringComparer.OrdinalIgnoreCase);
            if (runtime.Validate(name, null, strings).Count > 0) continue;
            var label = values.TryGetValue("name", out var named) ? named : values.Values.First();
            records.Add(new LocalRecord(name, label, values, sources));
        }
        return records;
    }

    private static (string Value, LocalSource Source)? Choice(FormProperty property, List<(string FileName, string Text)> documents)
    {
        var choices = property.Choices.OrderByDescending(choice => Math.Max(choice.Text.Length, choice.Value.Length));
        foreach (var document in documents)
        {
            foreach (var choice in choices)
            {
                if (Contains(document.Text, choice.Text) || Contains(document.Text, choice.Value))
                    return (choice.Value, new LocalSource(document.FileName, 1, "local text"));
            }
        }
        return null;
    }

    private static (string Value, LocalSource Source)? Labeled(FormProperty property, List<(string FileName, string Text)> documents)
    {
        var labels = property.Controls.Select(control => control.PageText)
            .Append(property.Name)
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var document in documents)
        {
            foreach (var line in document.Text.Split('\n'))
            {
                foreach (var label in labels)
                {
                    var match = Regex.Match(line.Trim(), $"^{Regex.Escape(label)}\\s*[:\\-]\\s*(.+)$", RegexOptions.IgnoreCase);
                    if (!match.Success) continue;
                    var value = match.Groups[1].Value.Trim();
                    if (property.Kind == "number" && !double.TryParse(value, out _)) continue;
                    if (property.Kind == "date" && !DateOnly.TryParse(value, out _)) continue;
                    if (value.Length == 0) continue;
                    return (value, new LocalSource(document.FileName, 1, "local text"));
                }
            }
        }
        return null;
    }

    private static bool Contains(string text, string term)
    {
        if (string.IsNullOrWhiteSpace(term)) return false;
        if (term.Length < 4)
            return Regex.IsMatch(text, $@"\b{Regex.Escape(term)}\b", RegexOptions.IgnoreCase);
        return text.Contains(term, StringComparison.OrdinalIgnoreCase);
    }

    private static string Plain(string reading)
    {
        try
        {
            using var document = JsonDocument.Parse(reading);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("text", out var text)
                && text.ValueKind == JsonValueKind.String)
                return text.GetString() ?? "";
        }
        catch (JsonException)
        {
            return reading;
        }
        return reading;
    }
}
