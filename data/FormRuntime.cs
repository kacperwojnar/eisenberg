using System.Text.Json;

namespace Eisenberg.Data;

public sealed record FormChoice(string Value, string Text);

public sealed record FormControlRef(string Key, string PageText, string Selector, int Frame, int Step);

public sealed record FormProperty(
    string Name,
    string Description,
    string Kind,
    bool Required,
    string Limits,
    string ShownWhen,
    IReadOnlyList<string> FillAfter,
    bool Ignored,
    string? ChoiceListId,
    bool ChoiceListComplete,
    IReadOnlyList<FormChoice> Choices,
    IReadOnlyList<FormControlRef> Controls);

public sealed record FormType(string Name, string? SubclassOf, bool Repeated, IReadOnlyList<FormProperty> OwnProperties);

public sealed record FieldProblem(string? Property, string Message);

public sealed record FillStep(
    string Property,
    string? Value,
    string PageText,
    string Selector,
    int Frame,
    int Step,
    string Kind,
    bool IncompleteList,
    bool ShownConditionally,
    IReadOnlyList<string> OptionTexts);

public sealed class FormRuntime
{
    private readonly Dictionary<string, FormType> _types;
    private readonly Dictionary<string, IReadOnlyList<FormChoice>> _lists;

    private FormRuntime(Dictionary<string, FormType> types)
    {
        _types = types;
        _lists = types.Values
            .SelectMany(type => type.OwnProperties)
            .Where(property => property.ChoiceListId is not null)
            .GroupBy(property => property.ChoiceListId!)
            .ToDictionary(group => group.Key, group => group.First().Choices);
    }

    public static FormRuntime Parse(string modelJson, IEnumerable<(string Id, bool Complete, string ChoicesJson)> lists)
    {
        var choices = lists.ToDictionary(list => list.Id, list => (list.Complete, Choices: ReadChoices(list.ChoicesJson)));
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(modelJson) ? """{"types":[]}""" : modelJson);
        var types = new Dictionary<string, FormType>(StringComparer.OrdinalIgnoreCase);
        if (document.RootElement.TryGetProperty("types", out var array))
        {
            foreach (var node in array.EnumerateArray())
            {
                var name = Text(node, "name");
                if (name.Length == 0) continue;
                var properties = new List<FormProperty>();
                if (node.TryGetProperty("properties", out var propertyNodes))
                {
                    foreach (var property in propertyNodes.EnumerateArray())
                        properties.Add(ReadProperty(property, choices));
                }
                types[name] = new FormType(
                    name,
                    Optional(node, "subclassOf"),
                    node.TryGetProperty("repeated", out var repeated) && repeated.ValueKind == JsonValueKind.True,
                    properties);
            }
        }
        return new FormRuntime(types);
    }

    public IReadOnlyList<FormProperty> PropertiesOf(string typeName, string? subclassName)
    {
        var name = string.IsNullOrWhiteSpace(subclassName) ? typeName : subclassName;
        if (!_types.TryGetValue(name, out var type)) return [];
        var chain = new List<FormType>();
        var guard = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (type is not null && guard.Add(type.Name))
        {
            chain.Add(type);
            type = type.SubclassOf is not null && _types.TryGetValue(type.SubclassOf, out var parent) ? parent : null;
        }
        chain.Reverse();
        var merged = new Dictionary<string, FormProperty>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in chain)
        {
            foreach (var property in item.OwnProperties)
                merged[property.Name] = property;
        }
        return merged.Values.ToList();
    }

    public IReadOnlyList<string> Validate(string typeName, string? subclassName, IReadOnlyDictionary<string, string?> values) =>
        Problems(typeName, subclassName, values).Select(problem => problem.Message).ToList();

    public IReadOnlyList<FieldProblem> Problems(string typeName, string? subclassName, IReadOnlyDictionary<string, string?> values)
    {
        var problems = new List<FieldProblem>();
        var type = string.IsNullOrWhiteSpace(subclassName) ? typeName : subclassName;
        if (!_types.ContainsKey(typeName))
            problems.Add(new FieldProblem(null, $"Unknown type {typeName}."));
        if (!string.IsNullOrWhiteSpace(subclassName) && !_types.ContainsKey(subclassName))
            problems.Add(new FieldProblem(null, $"Unknown subclass {subclassName}."));
        foreach (var property in PropertiesOf(typeName, subclassName))
        {
            if (property.Ignored) continue;
            values.TryGetValue(property.Name, out var value);
            var empty = string.IsNullOrWhiteSpace(value);
            if (property.Required && empty && property.ShownWhen.Length == 0)
                problems.Add(new FieldProblem(property.Name, $"{property.Name} is required."));
            if (empty) continue;
            if (property.ChoiceListComplete && property.Choices.Count > 0 && !property.Choices.Any(choice => Same(choice.Value, value) || Same(choice.Text, value)))
                problems.Add(new FieldProblem(property.Name, $"{property.Name} is not one of the stored choices."));
            if (property.Kind == "number" && !double.TryParse(value, out _))
                problems.Add(new FieldProblem(property.Name, $"{property.Name} is not a number."));
            if (property.Kind == "date" && !DateOnly.TryParse(value, out _))
                problems.Add(new FieldProblem(property.Name, $"{property.Name} is not a date."));
        }
        if (!string.IsNullOrWhiteSpace(subclassName) && !string.Equals(subclassName, type, StringComparison.OrdinalIgnoreCase) && !_types.ContainsKey(type))
            problems.Add(new FieldProblem(null, $"Unknown type {type}."));
        return problems;
    }

    public IReadOnlyList<FillStep> Plan(string typeName, string? subclassName, IReadOnlyDictionary<string, string?> values)
    {
        var properties = PropertiesOf(typeName, subclassName).Where(property => !property.Ignored).ToList();
        var order = Order(properties);
        var steps = new List<FillStep>();
        foreach (var property in order)
        {
            values.TryGetValue(property.Name, out var value);
            if (string.IsNullOrWhiteSpace(value)) continue;
            var control = property.Controls.OrderBy(item => item.Step).FirstOrDefault();
            var stored = property.Choices.FirstOrDefault(choice => Same(choice.Value, value) || Same(choice.Text, value));
            steps.Add(new FillStep(
                property.Name,
                stored?.Text ?? value,
                control?.PageText ?? "",
                control?.Selector ?? "",
                control?.Frame ?? 0,
                control?.Step ?? 1,
                property.Kind,
                property.ChoiceListId is not null && !property.ChoiceListComplete,
                property.ShownWhen.Length > 0,
                property.Choices.Select(choice => choice.Text).Where(text => text.Length > 0).ToArray()));
        }
        return steps;
    }

    private static List<FormProperty> Order(List<FormProperty> properties)
    {
        var byName = properties.ToDictionary(property => property.Name, StringComparer.OrdinalIgnoreCase);
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<FormProperty>();
        var guard = 0;
        while (ordered.Count < properties.Count && guard++ < properties.Count + 2)
        {
            var ready = properties.Where(property => !done.Contains(property.Name) && property.FillAfter.All(name => done.Contains(name) || !byName.ContainsKey(name))).ToList();
            if (ready.Count == 0)
                ready = properties.Where(property => !done.Contains(property.Name)).Take(1).ToList();
            foreach (var property in ready)
            {
                done.Add(property.Name);
                ordered.Add(property);
            }
        }
        return ordered;
    }

    private static FormProperty ReadProperty(JsonElement node, IReadOnlyDictionary<string, (bool Complete, IReadOnlyList<FormChoice> Choices)> lists)
    {
        var choiceId = Optional(node, "choiceListId");
        (bool Complete, IReadOnlyList<FormChoice> Choices) list = default;
        var known = choiceId is not null && lists.TryGetValue(choiceId, out list);
        var controls = new List<FormControlRef>();
        if (node.TryGetProperty("controls", out var controlNodes) && controlNodes.ValueKind == JsonValueKind.Array)
        {
            foreach (var control in controlNodes.EnumerateArray())
            {
                controls.Add(new FormControlRef(
                    Text(control, "key"),
                    Text(control, "pageText"),
                    Text(control, "selector"),
                    control.TryGetProperty("frame", out var frame) && frame.TryGetInt32(out var frameNumber) ? frameNumber : 0,
                    control.TryGetProperty("step", out var step) && step.TryGetInt32(out var stepNumber) ? stepNumber : 1));
            }
        }
        var after = new List<string>();
        if (node.TryGetProperty("fillAfter", out var fillAfter) && fillAfter.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in fillAfter.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } name)
                    after.Add(name);
            }
        }
        return new FormProperty(
            Text(node, "name"),
            Text(node, "description"),
            Text(node, "kind"),
            node.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.True,
            Text(node, "limits"),
            Text(node, "shownWhen"),
            after,
            node.TryGetProperty("ignored", out var ignored) && ignored.ValueKind == JsonValueKind.True,
            choiceId,
            known && list.Complete,
            known ? list.Choices ?? [] : [],
            controls);
    }

    public static IReadOnlyList<FormChoice> ReadChoices(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var array = root.ValueKind == JsonValueKind.Array
            ? root
            : root.TryGetProperty("choices", out var nested) ? nested : default;
        if (array.ValueKind != JsonValueKind.Array) return [];
        var choices = new List<FormChoice>();
        foreach (var item in array.EnumerateArray())
        {
            var value = Text(item, "value");
            var text = Text(item, "text");
            if (value.Length == 0 && text.Length == 0) continue;
            choices.Add(new FormChoice(value.Length == 0 ? text : value, text.Length == 0 ? value : text));
        }
        return choices;
    }

    private static bool Same(string? left, string? right) =>
        string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string Text(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static string? Optional(JsonElement node, string name)
    {
        if (!node.TryGetProperty(name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
