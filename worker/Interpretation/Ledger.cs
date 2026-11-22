using System.Text.Json.Nodes;

namespace Eisenberg.Worker.Interpretation;

sealed record Condition(string ControlKey, string Value, string Text);

sealed record OptionSet(string ParentKey, string ParentValue, IReadOnlyList<OptionSnapshot> Options, bool Complete);

sealed class ControlRecord
{
    public required string Key { get; init; }
    public required ControlSnapshot Latest { get; set; }
    public int FirstState { get; init; }
    public int Step { get; init; }
    public bool PresentAtStepStart { get; init; }
    public List<List<Condition>> ShownWhen { get; } = [];
    public Dictionary<string, OptionSet> OptionsWhen { get; } = [];
    public HashSet<string> OptionsDependOn { get; } = [];
    public Dictionary<string, OptionSnapshot> AllOptions { get; } = [];
    public bool OptionsCompleteEverywhere { get; set; } = true;
}

sealed record RecordedAction(int Sequence, string Tool, JsonObject Arguments, int StateBefore, int StateAfter, string? ControlKey, string? Value);

sealed record Observation(int Action, string ControlKey, string Value, IReadOnlyList<string> Appeared, IReadOnlyList<string> Disappeared, IReadOnlyList<string> OptionsChanged);

sealed record StepInfo(int Index, int FirstState, string Url, IReadOnlyList<string> Headings, IReadOnlyList<Condition> ValuesUsedToArrive);

sealed record SkippedItem(string ControlKey, string Label, string Reason);

// The record of a pass: every page state, every action, every control seen, and what is still unchecked.
sealed class Ledger
{
    public List<PageState> States { get; } = [];
    public List<RecordedAction> Actions { get; } = [];
    public Dictionary<string, ControlRecord> Controls { get; } = [];
    public List<string> ControlOrder { get; } = [];
    public List<Observation> Observations { get; } = [];
    public List<StepInfo> Steps { get; } = [];
    public HashSet<string> Tried { get; } = [];
    public HashSet<string> Read { get; } = [];
    public HashSet<string> Collected { get; } = [];
    public List<SkippedItem> Skipped { get; } = [];
    public List<string> Problems { get; } = [];
    public List<string> Sampled { get; } = [];
    public bool FinalStepReached { get; set; }

    public PageState? Current => States.Count == 0 ? null : States[^1];

    public static string TriedKey(string controlKey, string value) => controlKey + '\u001f' + value;

    public StateDiff Record(PageState state, string? tool, JsonObject? arguments, string? controlKey, string? value, bool movesOn)
    {
        var before = Current;
        state.Index = States.Count;
        var diff = before is null
            ? new StateDiff(state.Controls, [], [], state.Errors, false)
            : Inventory.Diff(before, state);
        var newStep = before is null || IsNewStep(before, diff, movesOn);
        if (newStep)
            Steps.Add(new StepInfo(Steps.Count, state.Index, state.Url, state.Headings, before is null ? [] : ValuesIn(before)));
        state.Step = Steps.Count - 1;
        States.Add(state);

        if (tool is not null)
            Actions.Add(new RecordedAction(Actions.Count + 1, tool, arguments ?? new JsonObject(), before?.Index ?? -1, state.Index, controlKey, value));

        var appeared = diff.Appeared.Select(control => control.Key).ToHashSet();
        foreach (var control in state.Controls)
        {
            if (!Controls.TryGetValue(control.Key, out var record))
            {
                record = new ControlRecord
                {
                    Key = control.Key,
                    Latest = control,
                    FirstState = state.Index,
                    Step = state.Step,
                    PresentAtStepStart = newStep
                };
                Controls[control.Key] = record;
                ControlOrder.Add(control.Key);
            }
            record.Latest = control;
            foreach (var option in control.Options)
                record.AllOptions.TryAdd(option.Value, option);
            if (control.Options.Count > 0 && !control.OptionsComplete)
                record.OptionsCompleteEverywhere = false;
            if (control.Kind is "combobox" or "listbox" && control.Options.Count > 0)
                Read.Add(control.Key);
            if (!newStep && controlKey is not null && value is not null && appeared.Contains(control.Key))
            {
                var chain = ChainFor(controlKey, value);
                if (!record.ShownWhen.Any(existing => existing.SequenceEqual(chain)))
                    record.ShownWhen.Add(chain);
            }
        }

        if (controlKey is not null && value is not null && before is not null && !newStep)
        {
            var parentBefore = before.Find(controlKey);
            foreach (var changed in diff.OptionsChanged)
            {
                var record = Controls[changed.Key];
                record.OptionsDependOn.Add(controlKey);
                record.OptionsWhen[controlKey + "=" + value] = new OptionSet(controlKey, value, changed.Options, changed.OptionsComplete);
                var previous = before.Find(changed.Key);
                var previousValue = parentBefore?.Selected.FirstOrDefault();
                if (previous is not null && previousValue is not null && !record.OptionsWhen.ContainsKey(controlKey + "=" + previousValue))
                    record.OptionsWhen[controlKey + "=" + previousValue] = new OptionSet(controlKey, previousValue, previous.Options, previous.OptionsComplete);
            }
            Observations.Add(new Observation(Actions.Count, controlKey, value,
                diff.Appeared.Select(control => control.Key).ToList(),
                diff.Disappeared.Select(control => control.Key).ToList(),
                diff.OptionsChanged.Select(control => control.Key).ToList()));
            Tried.Add(TriedKey(controlKey, value));
        }
        return diff;
    }

    public JsonObject Next()
    {
        var state = Current ?? throw new InvalidOperationException("No page state yet.");
        if (state.CookieBanner)
            return Item("dismiss-banner", null, "A cookie or consent banner is open. Close it, accepting only what is needed, then call next_item.");

        foreach (var control in state.Controls)
        {
            if (!control.IsChoice || control.Disabled || IsSkipped(control.Key)) continue;
            if (control.Kind is "combobox" or "listbox" && !control.OptionsComplete && !Read.Contains(control.Key))
            {
                return Item("read-options", control, control.Scrollable
                    ? "The list scrolls. Open it if it is closed, then call collect_options with this controlKey."
                    : "Open this control so its options are rendered: click it, or type one letter if it is a search box. Then call next_item. If no options appear, call skip_item.");
            }
            if (Untried(control).Count == 0) continue;
            if (control.CanSweep)
                return Item("try-options", control, "Call try_all_options with this controlKey.");
            return Item("try-options-by-hand", control,
                "Choose each option of this control in turn with browser actions; the system records what each choice changes. If there are many options or choosing them is not possible, call skip_item with the reason.");
        }

        foreach (var record in HiddenWithUntried(state))
        {
            return Item("try-options", record.Latest,
                "This control is hidden now. Call try_all_options with this controlKey; the system first sets the values that show it.");
        }

        if (FinalStepReached)
            return Item("done", null, "Everything on this step was checked and the final step was reached. Call finish with reason final-step-reached.");
        return Item("continue", null,
            "Everything on this step was checked. Fill every required field with a plausible test value, then press the button that moves to the next step. " +
            "Never press the final submit. If only the final submit is left, call finish with reason final-step-reached.");
    }

    public List<OptionSnapshot> Untried(ControlSnapshot control) =>
        control.Options.Where(option => !option.Disabled && option.Value.Length > 0 && !Tried.Contains(TriedKey(control.Key, option.Value))).ToList();

    public List<string> PendingDescriptions()
    {
        var state = Current;
        if (state is null) return [];
        var pending = new List<string>();
        foreach (var control in state.Controls.Where(control => control.IsChoice && !control.Disabled && !IsSkipped(control.Key)))
        {
            if (control.Kind is "combobox" or "listbox" && !control.OptionsComplete && !Read.Contains(control.Key))
                pending.Add($"options of \"{control.Label}\" not read");
            var untried = Untried(control).Count;
            if (untried > 0)
                pending.Add($"{untried} values of \"{control.Label}\" not tried");
        }
        foreach (var record in HiddenWithUntried(state))
            pending.Add($"{Untried(record.Latest).Count} values of \"{record.Latest.Label}\" not tried (hidden now)");
        return pending;
    }

    // Controls of this step that a choice revealed earlier and that are hidden again, with values still untried.
    private IEnumerable<ControlRecord> HiddenWithUntried(PageState state) =>
        ControlOrder.Select(key => Controls[key]).Where(record =>
            record.Step == state.Step &&
            state.Find(record.Key) is null &&
            record.ShownWhen.Count > 0 &&
            record.Latest.CanSweep &&
            !record.Latest.Disabled &&
            !IsSkipped(record.Key) &&
            Untried(record.Latest).Count > 0);

    public void Skip(string controlKey, string reason)
    {
        var label = Controls.TryGetValue(controlKey, out var record) ? record.Latest.Label : controlKey;
        Skipped.Add(new SkippedItem(controlKey, label, reason));
    }

    private bool IsSkipped(string controlKey) => Skipped.Any(item => item.ControlKey == controlKey);

    private List<Condition> ChainFor(string controlKey, string value)
    {
        var chain = new List<Condition>();
        if (Controls.TryGetValue(controlKey, out var parent))
        {
            if (parent.ShownWhen.Count > 0)
                chain.AddRange(parent.ShownWhen[0]);
            var text = parent.Latest.Options.FirstOrDefault(option => option.Value == value)?.Text ?? value;
            chain.Add(new Condition(controlKey, value, $"\"{parent.Latest.Label}\" = \"{text}\""));
        }
        else
        {
            chain.Add(new Condition(controlKey, value, $"{controlKey} = {value}"));
        }
        return chain;
    }

    private static bool IsNewStep(PageState before, StateDiff diff, bool movesOn)
    {
        if (before.Controls.Count == 0) return diff.Appeared.Count > 0 && (movesOn || diff.UrlChanged);
        var share = (double)diff.Disappeared.Count / before.Controls.Count;
        return (movesOn || diff.UrlChanged) && share > 0.5 && diff.Appeared.Count > 0;
    }

    private static List<Condition> ValuesIn(PageState state) =>
        state.Controls
            .Where(control => control.HasValue)
            .Select(control => new Condition(control.Key, string.Join(",", control.Selected),
                control.IsChoice
                    ? $"\"{control.Label}\" = \"{string.Join(", ", control.Selected.Select(value => control.Options.FirstOrDefault(option => option.Value == value)?.Text ?? value))}\""
                    : $"\"{control.Label}\" filled"))
            .ToList();

    private JsonObject Item(string kind, ControlSnapshot? control, string instruction)
    {
        var item = new JsonObject
        {
            ["kind"] = kind,
            ["step"] = Steps.Count,
            ["instruction"] = instruction
        };
        if (control is not null)
        {
            var untried = Untried(control);
            item["control"] = new JsonObject
            {
                ["key"] = control.Key,
                ["kind"] = control.Kind,
                ["label"] = control.Label,
                ["selector"] = control.Selector,
                ["frame"] = control.Frame,
                ["optionCount"] = control.OptionCount,
                ["untried"] = new JsonArray(untried.Take(30).Select(option => (JsonNode?)option.Text).ToArray())
            };
        }
        return item;
    }
}
