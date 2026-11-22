using System.Text.Json.Nodes;

namespace Eisenberg.Worker.Interpretation;

sealed record BuiltChoiceList(string ControlKey, bool IsComplete, JsonArray DependsOn, JsonObject Choices);

sealed record BuiltModel(JsonObject Model, List<BuiltChoiceList> ChoiceLists, List<string> Unmapped)
{
    public JsonObject WithChoiceListIds(IReadOnlyDictionary<string, Guid> ids)
    {
        var model = (JsonObject)Model.DeepClone();
        foreach (var type in Json.Array(model["types"]))
        {
            foreach (var property in Json.Array(type?["properties"]))
            {
                if (property is not JsonObject item) continue;
                var key = Json.Str(item["choiceListControl"]);
                item.Remove("choiceListControl");
                item["choiceListId"] = ids.TryGetValue(key, out var id) ? id.ToString() : null;
            }
        }
        return model;
    }
}

// Names, descriptions, and grouping come from the language model; controls, options, and conditions come from the record.
sealed class ModelBuilder(IChatModel model)
{
    private const string System = """
        You turn the recorded inventory of a web form into a data model for a program. The input lists every control the pass saw: its key, kind, label, the question text near it, its options, and the conditions under which it appeared. The admin's description says what the form is for.

        Return one JSON object and nothing else:
        {
          "types": [
            {
              "name": "PascalCase type name",
              "description": "what one record of this type is",
              "subclassOf": null,
              "repeated": false,
              "properties": [
                {
                  "name": "camelCase property name",
                  "description": "the fact this property holds, precise enough to judge a value found in a document against it",
                  "kind": "text | number | date | choice | multi-choice | boolean | file",
                  "required": true,
                  "limits": "format, range, or length, or an empty string",
                  "controls": ["c1"],
                  "shownWhen": "the condition in plain language, or an empty string",
                  "fillAfter": ["names of properties that must be filled before this one"]
                }
              ]
            }
          ]
        }

        Rules:
        - Every control key appears in the controls of exactly one property. Never drop a control. Consents and choices that only steer the form are properties too.
        - A property name is not the question text. It names the fact, in English camelCase.
        - When the same group of controls repeats for different people or objects, make one type with repeated set to true. Put the controls of every occurrence into the controls array of the matching property, in order.
        - When a choice switches between different sets of controls, model the sets as subclasses of a common type, with subclassOf naming the parent.
        - Do not list options. The system attaches the recorded options to each property.
        """;

    public async Task<BuiltModel> BuildAsync(Ledger ledger, string description, string address, CancellationToken cancellationToken)
    {
        var aliases = ledger.ControlOrder.Select((key, index) => (key, alias: "c" + (index + 1))).ToList();
        var toAlias = aliases.ToDictionary(pair => pair.key, pair => pair.alias);
        var fromAlias = aliases.ToDictionary(pair => pair.alias, pair => pair.key);
        var input = new JsonObject
        {
            ["address"] = address,
            ["description"] = description,
            ["steps"] = new JsonArray(ledger.Steps.Select(step => (JsonNode?)new JsonObject
            {
                ["step"] = step.Index + 1,
                ["url"] = step.Url,
                ["headings"] = new JsonArray(step.Headings.Take(5).Select(heading => (JsonNode?)heading).ToArray())
            }).ToArray()),
            ["controls"] = new JsonArray(ledger.ControlOrder.Select(key => (JsonNode?)Describe(ledger.Controls[key], toAlias)).ToArray())
        };

        var history = new List<ChatItem> { new UserText(input.ToJsonString()) };
        JsonNode? answer = null;
        for (var attempt = 0; attempt < 2 && answer is null; attempt++)
        {
            var turn = await model.NextAsync(System, history, [], 32000, cancellationToken);
            answer = Json.FirstValue(turn.Text, node => node?["types"] is JsonArray);
            if (answer is not null) break;
            history.Add(turn);
            history.Add(new UserText("Return only the JSON object with the types array, nothing else."));
        }
        if (answer is null)
            throw new InvalidOperationException("The model builder did not return a model.");
        return Assemble(answer, ledger, fromAlias, address);
    }

    private static JsonObject Describe(ControlRecord record, IReadOnlyDictionary<string, string> toAlias)
    {
        var control = record.Latest;
        var node = new JsonObject
        {
            ["key"] = toAlias[record.Key],
            ["kind"] = control.Kind,
            ["label"] = control.Label,
            ["question"] = control.Question,
            ["heading"] = control.Heading,
            ["step"] = record.Step + 1,
            ["required"] = control.Required
        };
        if (control.Constraints.Count > 0) node["constraints"] = control.Constraints.DeepClone();
        if (HasChoices(record))
        {
            node["optionCount"] = record.AllOptions.Count;
            node["options"] = new JsonArray(record.AllOptions.Values.Take(15).Select(option => (JsonNode?)option.Text).ToArray());
        }
        if (record.ShownWhen.Count > 0)
            node["shownWhen"] = new JsonArray(record.ShownWhen.Select(chain => (JsonNode?)string.Join(" and ", chain.Select(condition => condition.Text))).ToArray());
        if (record.OptionsDependOn.Count > 0)
            node["optionsDependOn"] = new JsonArray(record.OptionsDependOn.Where(toAlias.ContainsKey).Select(key => (JsonNode?)toAlias[key]).ToArray());
        return node;
    }

    private static BuiltModel Assemble(JsonNode answer, Ledger ledger, IReadOnlyDictionary<string, string> fromAlias, string address)
    {
        var mapped = new HashSet<string>();
        var types = new JsonArray();
        foreach (var type in Json.Array(answer["types"]))
        {
            if (type is null) continue;
            var properties = new JsonArray();
            foreach (var property in Json.Array(type["properties"]))
            {
                if (property is null) continue;
                var keys = Json.Array(property["controls"]).Select(Json.Str).Where(fromAlias.ContainsKey).Select(alias => fromAlias[alias]).Distinct().ToList();
                mapped.UnionWith(keys);
                var first = keys.Count > 0 ? ledger.Controls[keys[0]] : null;
                properties.Add(new JsonObject
                {
                    ["name"] = Json.Str(property["name"]),
                    ["description"] = Json.Str(property["description"]),
                    ["kind"] = Json.Str(property["kind"]),
                    ["required"] = Json.Bool(property["required"]),
                    ["limits"] = Json.Str(property["limits"]),
                    ["shownWhen"] = Json.Str(property["shownWhen"]),
                    ["fillAfter"] = property["fillAfter"]?.DeepClone() as JsonArray ?? new JsonArray(),
                    ["ignored"] = false,
                    ["controls"] = new JsonArray(keys.Select(key => (JsonNode?)ControlInfo(ledger.Controls[key])).ToArray()),
                    ["choiceListControl"] = first is not null && HasChoices(first) ? first.Key : null
                });
            }
            types.Add(new JsonObject
            {
                ["name"] = Json.Str(type["name"]),
                ["description"] = Json.Str(type["description"]),
                ["subclassOf"] = type["subclassOf"] is null ? null : Json.Str(type["subclassOf"]),
                ["repeated"] = Json.Bool(type["repeated"]),
                ["properties"] = properties
            });
        }

        var unmapped = ledger.ControlOrder.Where(key => !mapped.Contains(key)).ToList();
        var model = new JsonObject
        {
            ["form"] = new JsonObject
            {
                ["address"] = address,
                ["steps"] = new JsonArray(ledger.Steps.Select(step => (JsonNode?)new JsonObject
                {
                    ["index"] = step.Index + 1,
                    ["url"] = step.Url,
                    ["headings"] = new JsonArray(step.Headings.Take(5).Select(heading => (JsonNode?)heading).ToArray()),
                    ["structure"] = ledger.States[step.FirstState].Fingerprint,
                    ["controls"] = new JsonArray(ledger.Controls.Values.Where(record => record.Step == step.Index).Select(record => (JsonNode?)record.Key).ToArray())
                }).ToArray())
            },
            ["types"] = types,
            ["unmapped"] = new JsonArray(unmapped.Select(key => (JsonNode?)ControlInfo(ledger.Controls[key])).ToArray())
        };
        return new BuiltModel(model, ChoiceLists(ledger), unmapped);
    }

    private static List<BuiltChoiceList> ChoiceLists(Ledger ledger)
    {
        var lists = new List<BuiltChoiceList>();
        foreach (var key in ledger.ControlOrder)
        {
            var record = ledger.Controls[key];
            if (!HasChoices(record)) continue;
            var byParent = new JsonArray(record.OptionsWhen.Values.Select(set => (JsonNode?)new JsonObject
            {
                ["control"] = set.ParentKey,
                ["value"] = set.ParentValue,
                ["complete"] = set.Complete,
                ["choices"] = Choices(set.Options)
            }).ToArray());
            var complete = (record.OptionsCompleteEverywhere || ledger.Collected.Contains(key)) &&
                record.OptionsDependOn.All(parent => ParentCovered(ledger, record, parent));
            lists.Add(new BuiltChoiceList(
                key,
                complete,
                new JsonArray(record.OptionsDependOn.Select(parent => (JsonNode?)new JsonObject
                {
                    ["control"] = parent,
                    ["label"] = ledger.Controls.TryGetValue(parent, out var other) ? other.Latest.Label : parent
                }).ToArray()),
                new JsonObject { ["choices"] = Choices(record.AllOptions.Values), ["byParent"] = byParent }));
        }
        return lists;
    }

    private static bool ParentCovered(Ledger ledger, ControlRecord record, string parent) =>
        ledger.Controls.TryGetValue(parent, out var parentRecord) &&
        parentRecord.AllOptions.Keys.Where(value => value.Length > 0)
            .All(value => record.OptionsWhen.TryGetValue(parent + "=" + value, out var set) && set.Complete);

    private static JsonArray Choices(IEnumerable<OptionSnapshot> options) =>
        new(options.Select(option => (JsonNode?)new JsonObject { ["value"] = option.Value, ["text"] = option.Text }).ToArray());

    private static bool HasChoices(ControlRecord record) =>
        record.AllOptions.Count > 0 && record.Latest.Kind is not ("checkbox" or "switch" or "toggle");

    private static JsonObject ControlInfo(ControlRecord record)
    {
        var control = record.Latest;
        return new JsonObject
        {
            ["key"] = record.Key,
            ["pageText"] = control.Label,
            ["question"] = control.Question,
            ["name"] = control.Name,
            ["kind"] = control.Kind,
            ["selector"] = control.Selector,
            ["frame"] = control.Frame,
            ["step"] = record.Step + 1,
            ["required"] = control.Required,
            ["conditions"] = new JsonArray(record.ShownWhen.Select(chain => (JsonNode?)new JsonArray(chain.Select(condition => (JsonNode?)new JsonObject
            {
                ["control"] = condition.ControlKey,
                ["value"] = condition.Value,
                ["text"] = condition.Text
            }).ToArray())).ToArray())
        };
    }
}
