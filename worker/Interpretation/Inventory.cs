using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Eisenberg.Worker.Interpretation;

sealed record OptionSnapshot(string Value, string Text, string? Selector, bool Disabled);

sealed record ControlSnapshot(
    string Key,
    int Frame,
    string Kind,
    string Tag,
    string Type,
    string Name,
    string Question,
    string Heading,
    string Selector,
    bool Required,
    bool Disabled,
    JsonObject Constraints,
    IReadOnlyList<OptionSnapshot> Options,
    bool OptionsComplete,
    int OptionCount,
    IReadOnlyList<string> Selected,
    bool HasValue,
    bool Scrollable)
{
    public bool IsChoice => Kind is "select" or "multi-select" or "radio-group" or "checkbox-group" or "checkbox"
        or "switch" or "toggle" or "tab-group" or "listbox" or "combobox";

    public bool CanSweep => Kind is "select" or "multi-select" or "radio-group" or "checkbox-group" or "checkbox"
        or "switch" or "toggle" or "tab-group" or "listbox";

    public string OptionsSignature => string.Join('\u001f', Options.Select(option => option.Value));

    public string Label =>
        Name.Length > 0 && Question.Length > 0 && !Question.Contains(Name, StringComparison.OrdinalIgnoreCase) ? $"{Question} / {Name}"
        : Name.Length > 0 ? Name
        : Question;
}

sealed record ButtonSnapshot(int Frame, string Text, string Selector, bool Submits, bool Disabled);

sealed class PageState
{
    public int Index { get; set; }
    public int Step { get; set; }
    public string Fingerprint { get; init; } = "";
    public string Url { get; init; } = "";
    public string Title { get; init; } = "";
    public List<string> Headings { get; init; } = [];
    public List<string> Errors { get; init; } = [];
    public bool CookieBanner { get; init; }
    public List<ControlSnapshot> Controls { get; init; } = [];
    public List<ButtonSnapshot> Buttons { get; init; } = [];
    public List<string> FrameErrors { get; init; } = [];
    public bool Truncated { get; init; }
    public JsonNode Raw { get; init; } = new JsonObject();

    public ControlSnapshot? Find(string key) => Controls.FirstOrDefault(control => control.Key == key);
}

sealed record StateDiff(
    IReadOnlyList<ControlSnapshot> Appeared,
    IReadOnlyList<ControlSnapshot> Disappeared,
    IReadOnlyList<ControlSnapshot> OptionsChanged,
    IReadOnlyList<string> NewErrors,
    bool UrlChanged)
{
    public bool Changed => Appeared.Count + Disappeared.Count + OptionsChanged.Count > 0;

    public string Summary(int max = 12)
    {
        if (!Changed && NewErrors.Count == 0) return UrlChanged ? "The address changed, the controls did not." : "Nothing changed.";
        var parts = new List<string>();
        if (Appeared.Count > 0) parts.Add($"appeared {Appeared.Count}: " + List(Appeared, max));
        if (Disappeared.Count > 0) parts.Add($"disappeared {Disappeared.Count}: " + List(Disappeared, max));
        if (OptionsChanged.Count > 0) parts.Add($"options changed {OptionsChanged.Count}: " + List(OptionsChanged, max));
        if (NewErrors.Count > 0) parts.Add("messages: " + string.Join(" | ", NewErrors.Take(5)));
        return string.Join("; ", parts) + ".";
    }

    private static string List(IReadOnlyList<ControlSnapshot> controls, int max) =>
        string.Join(", ", controls.Take(max).Select(control => $"\"{Json.Trim(control.Label, 60)}\" ({control.Kind})")) +
        (controls.Count > max ? $" and {controls.Count - max} more" : "");
}

static class Inventory
{
    private static readonly Regex GeneratedId = new(@"\d{4,}|[a-f0-9]{8,}|^(ember|react|mui|radix|headlessui|ng-)|:r[0-9a-z]+:", RegexOptions.IgnoreCase);

    public static PageState Parse(JsonNode raw)
    {
        var controls = new List<ControlSnapshot>();
        var buttons = new List<ButtonSnapshot>();
        var headings = new List<string>();
        var errors = new List<string>();
        var frameErrors = new List<string>();
        var cookieBanner = false;
        var truncated = false;
        var seen = new Dictionary<string, int>();

        foreach (var frame in Json.Array(raw["frames"]))
        {
            if (frame is null) continue;
            var index = Json.Int(frame["index"]);
            if (frame["error"] is not null)
            {
                frameErrors.Add($"frame {index} {Json.Str(frame["url"])}: {Json.Str(frame["error"])}");
                continue;
            }
            headings.AddRange(Json.Array(frame["headings"]).Select(Json.Str));
            errors.AddRange(Json.Array(frame["errors"]).Select(Json.Str));
            cookieBanner |= Json.Bool(frame["cookieBanner"]);
            truncated |= Json.Bool(frame["truncated"]?["controls"]);
            foreach (var item in Json.Array(frame["controls"]))
            {
                if (item is null) continue;
                var control = ToControl(index, item);
                var count = seen.GetValueOrDefault(control.Key) + 1;
                seen[control.Key] = count;
                controls.Add(count == 1 ? control : control with { Key = control.Key + "#" + count });
            }
            foreach (var item in Json.Array(frame["buttons"]))
            {
                if (item is null) continue;
                buttons.Add(new ButtonSnapshot(index, Json.Str(item["text"]), Json.Str(item["selector"]), Json.Bool(item["submits"]), Json.Bool(item["disabled"])));
            }
        }

        return new PageState
        {
            Fingerprint = Fingerprint(controls, headings),
            Url = Json.Str(raw["url"]),
            Title = Json.Str(raw["title"]),
            Headings = headings.Distinct().ToList(),
            Errors = errors.Distinct().ToList(),
            CookieBanner = cookieBanner,
            Controls = controls,
            Buttons = buttons,
            FrameErrors = frameErrors,
            Truncated = truncated,
            Raw = raw
        };
    }

    public static StateDiff Diff(PageState before, PageState after)
    {
        var old = before.Controls.ToDictionary(control => control.Key);
        var fresh = after.Controls.ToDictionary(control => control.Key);
        var appeared = after.Controls.Where(control => !old.ContainsKey(control.Key)).ToList();
        var disappeared = before.Controls.Where(control => !fresh.ContainsKey(control.Key)).ToList();
        var changed = after.Controls.Where(control => old.TryGetValue(control.Key, out var previous) &&
            previous.OptionsSignature != control.OptionsSignature && control.Kind is not ("checkbox" or "switch" or "toggle")).ToList();
        var newErrors = after.Errors.Except(before.Errors).ToList();
        return new StateDiff(appeared, disappeared, changed, newErrors, !SameAddress(before.Url, after.Url));
    }

    public static bool SameAddress(string left, string right) =>
        string.Equals(Strip(left), Strip(right), StringComparison.OrdinalIgnoreCase);

    private static string Strip(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;
        return uri.GetLeftPart(UriPartial.Path) + uri.Fragment;
    }

    private static ControlSnapshot ToControl(int frame, JsonNode item)
    {
        var kind = Json.Str(item["kind"]);
        var id = Json.Str(item["id"]);
        var nameAttribute = Json.Str(item["nameAttribute"]);
        var testId = Json.Str(item["testId"]);
        var name = Json.Str(item["name"]);
        var question = Json.Str(item["question"]);
        var identity = id.Length > 0 && !GeneratedId.IsMatch(id) ? "id:" + id
            : nameAttribute.Length > 0 ? "name:" + nameAttribute
            : testId.Length > 0 ? "test:" + testId
            : "text:" + Normalize(question) + "|" + Normalize(name);
        var options = Json.Array(item["options"]).Where(option => option is not null).Select(option => new OptionSnapshot(
            Json.Str(option!["value"]),
            Json.Str(option["text"]),
            option["selector"] is null ? null : Json.Str(option["selector"]),
            Json.Bool(option["disabled"]))).ToList();
        return new ControlSnapshot(
            $"{frame}|{kind}|{identity}",
            frame,
            kind,
            Json.Str(item["tag"]),
            Json.Str(item["type"]),
            name,
            question,
            Json.Str(item["heading"]),
            Json.Str(item["selector"]),
            Json.Bool(item["required"]),
            Json.Bool(item["disabled"]),
            item["constraints"]?.DeepClone() as JsonObject ?? new JsonObject(),
            options,
            item["optionsComplete"] is null ? options.Count > 0 : Json.Bool(item["optionsComplete"]),
            item["optionCount"] is null ? options.Count : Json.Int(item["optionCount"]),
            Json.Array(item["selected"]).Select(Json.Str).ToList(),
            Json.Bool(item["hasValue"]),
            Json.Bool(item["scrollable"]));
    }

    private static string Normalize(string text) => Regex.Replace(text.ToLowerInvariant(), @"\s+", " ").Trim();

    private static string Fingerprint(IEnumerable<ControlSnapshot> controls, IEnumerable<string> headings)
    {
        var text = string.Join('\n', controls.Select(control => control.Key + "=" + control.OptionsSignature).Order()) + "\n" + string.Join('\n', headings);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
    }
}
