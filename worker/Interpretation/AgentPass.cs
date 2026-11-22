using System.Diagnostics;
using System.Text.Json.Nodes;
using Eisenberg.Data;

namespace Eisenberg.Worker.Interpretation;

sealed record PassSettings(int MaxActions, TimeSpan MaxDuration, int MaxSweepOptions);

sealed record VisualTarget(int X, int Y, string Kind);

sealed record PassOutcome(string StoppedBecause, string Summary);

interface IRunRecorder
{
    Task OnStateAsync(PageState state, McpImage? screenshot, CancellationToken cancellationToken);
    Task SetStatusAsync(string status, string text, string? liveViewUrl, CancellationToken cancellationToken);
    Task<bool> WaitForSignInAsync(TimeSpan timeout, CancellationToken cancellationToken);
    Task CountsAsync(int actions, int states, CancellationToken cancellationToken);
}

sealed class AgentPass(
    BrowserSession session,
    IChatModel model,
    Ledger ledger,
    IRunRecorder recorder,
    PassSettings settings,
    string address,
    string description,
    ILogger logger)
{
    private static readonly HashSet<string> BrowserTools =
    [
        "browser_navigate", "browser_navigate_back", "browser_snapshot", "browser_find", "browser_click", "browser_type",
        "browser_select_option", "browser_press_key", "browser_hover", "browser_wait_for", "browser_handle_dialog",
        "browser_fill_form", "browser_tabs"
    ];

    private static readonly HashSet<string> ActionTools =
    [
        "browser_navigate", "browser_navigate_back", "browser_click", "browser_type", "browser_select_option",
        "browser_press_key", "browser_hover", "browser_handle_dialog", "browser_fill_form", "browser_tabs"
    ];

    private static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(15);

    private readonly HashSet<string> _fingerprints = [];
    private readonly HashSet<int> _visualSteps = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private PassOutcome? _outcome;

    public async Task<PassOutcome> RunAsync(CancellationToken cancellationToken)
    {
        var tools = BuildTools();
        var system = SystemPrompt();
        var start = new JsonObject { ["url"] = address };
        await session.CallAsync("browser_navigate", (JsonObject)start.DeepClone(), cancellationToken);
        await CaptureAsync("browser_navigate", start, null, null, true, cancellationToken);

        var history = new List<ChatItem> { new UserText("The browser is on the start address. Call next_item.") };
        var idle = 0;
        while (_outcome is null)
        {
            if (OverBudget(out var reason))
            {
                _outcome = new PassOutcome("budget", reason);
                break;
            }
            var turn = await model.NextAsync(system, Window(history), tools, 2048, cancellationToken);
            history.Add(turn);
            if (turn.Calls.Count == 0)
            {
                if (++idle >= 3)
                {
                    _outcome = new PassOutcome("agent-stopped", "The agent stopped calling tools: " + Json.Trim(turn.Text, 300));
                    break;
                }
                history.Add(new UserText("Use the tools. Call next_item, or finish."));
                continue;
            }
            idle = 0;
            var results = new List<ToolResult>();
            foreach (var call in turn.Calls)
            {
                results.Add(_outcome is null
                    ? await ExecuteAsync(call, cancellationToken)
                    : new ToolResult(call.Id, "Not run: the pass has ended.", true));
            }
            history.Add(new ToolResults(results));
        }
        return _outcome;
    }

    private async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken)
    {
        try
        {
            var text = call.Name switch
            {
                "next_item" => await NextItemAsync(cancellationToken),
                "try_all_options" => await TryAllOptionsAsync(Json.Str(call.Arguments["controlKey"]), cancellationToken),
                "collect_options" => await CollectOptionsAsync(Json.Str(call.Arguments["controlKey"]), cancellationToken),
                "skip_item" => Skip(call.Arguments),
                "request_sign_in" => await SignInAsync(Json.Str(call.Arguments["reason"]), cancellationToken),
                "report_problem" => Problem(call.Arguments),
                "finish" => Finish(call.Arguments),
                _ when BrowserTools.Contains(call.Name) => await BrowserAsync(call, cancellationToken),
                _ => throw new InvalidOperationException("Unknown tool " + call.Name)
            };
            return new ToolResult(call.Id, text);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Tool {Tool} failed", call.Name);
            return new ToolResult(call.Id, "Failed: " + Json.Trim(ex.Message, 400), true);
        }
    }

    private async Task<string> BrowserAsync(ToolCall call, CancellationToken cancellationToken)
    {
        var arguments = (JsonObject)call.Arguments.DeepClone();
        var result = await session.CallAsync(call.Name, (JsonObject)arguments.DeepClone(), cancellationToken);
        if (result.IsError) return "Failed: " + Json.Trim(result.Text, 2000);
        if (!ActionTools.Contains(call.Name)) return Json.Trim(result.Text, call.Name == "browser_snapshot" ? 50000 : 4000);

        var (controlKey, value, pressedButton) = Attribute(call.Name, arguments);
        var movesOn = call.Name is "browser_navigate" or "browser_navigate_back" or "browser_press_key" || pressedButton;
        var diff = await CaptureAsync(call.Name, arguments, controlKey, value, movesOn, cancellationToken);
        return Json.Trim(result.Text, 1500) + "\nSystem: " + diff.Summary();
    }

    private async Task<string> TryAllOptionsAsync(string key, CancellationToken cancellationToken)
    {
        var control = ledger.Current?.Find(key) ?? await RevealAsync(key, cancellationToken);
        if (control is null) return "That control is not on the page now and could not be shown. Call skip_item with the reason, then next_item.";
        if (!control.CanSweep) return "This control cannot be set automatically. Choose its options with browser actions, or call skip_item.";

        var options = ledger.Untried(control);
        var sampled = options.Count > settings.MaxSweepOptions;
        if (sampled)
        {
            options = Sample(options, settings.MaxSweepOptions);
            ledger.Sampled.Add($"\"{control.Label}\": {options.Count} of {control.Options.Count} values tried");
        }

        var original = control.Selected.Where(value => value.Length > 0).ToList();
        var startStep = ledger.Current!.Step;
        var lines = new List<string>();
        foreach (var option in options)
        {
            if (OverBudget(out _))
            {
                lines.Add("Stopped: the budget of the run is used up.");
                break;
            }
            var current = ledger.Current?.Find(key);
            if (current is null)
            {
                lines.Add("The control disappeared, so the remaining values were not tried.");
                break;
            }
            if (current.Kind == "checkbox-group")
            {
                lines.Add(await ToggleTwiceAsync(current, option, cancellationToken));
                continue;
            }
            var error = await SetAsync(current, option.Value, cancellationToken);
            if (error is not null)
            {
                ledger.Tried.Add(Ledger.TriedKey(key, option.Value));
                lines.Add($"\"{option.Text}\": could not be set ({error})");
                continue;
            }
            var diff = await CaptureAsync("set-value", SetArguments(current, option.Value), key, option.Value, false, cancellationToken);
            lines.Add($"\"{option.Text}\": {diff.Summary(6)}");
            if (ledger.Current!.Step != startStep)
            {
                lines.Add("The page moved to another step, so the remaining values were not tried.");
                break;
            }
        }

        var after = ledger.Current?.Find(key);
        if (after is not null && original.Count > 0 && after.Kind is "select" or "radio-group" or "tab-group" or "listbox" && !after.Selected.SequenceEqual(original))
        {
            if (await SetAsync(after, original[0], cancellationToken) is null)
                await CaptureAsync("set-value", SetArguments(after, original[0]), key, original[0], false, cancellationToken);
        }
        if (sampled)
        {
            foreach (var rest in ledger.Untried(control))
                ledger.Tried.Add(Ledger.TriedKey(key, rest.Value));
        }
        return string.Join('\n', lines);
    }

    private async Task<ControlSnapshot?> RevealAsync(string key, CancellationToken cancellationToken)
    {
        if (!ledger.Controls.TryGetValue(key, out var record) || record.ShownWhen.Count == 0) return null;
        foreach (var condition in record.ShownWhen[0])
        {
            var parent = ledger.Current?.Find(condition.ControlKey);
            if (parent is null) return null;
            if (parent.Selected.Contains(condition.Value)) continue;
            if (await SetAsync(parent, condition.Value, cancellationToken) is not null) return null;
            await CaptureAsync("set-value", SetArguments(parent, condition.Value), parent.Key, condition.Value, false, cancellationToken);
        }
        return ledger.Current?.Find(key);
    }

    private async Task<string> ToggleTwiceAsync(ControlSnapshot group, OptionSnapshot option, CancellationToken cancellationToken)
    {
        if (await SetAsync(group, option.Value, cancellationToken) is { } error)
        {
            ledger.Tried.Add(Ledger.TriedKey(group.Key, option.Value));
            return $"\"{option.Text}\": could not be set ({error})";
        }
        var on = await CaptureAsync("set-value", SetArguments(group, option.Value), group.Key, option.Value, false, cancellationToken);
        var again = ledger.Current?.Find(group.Key);
        if (again is not null && await SetAsync(again, option.Value, cancellationToken) is null)
            await CaptureAsync("set-value", SetArguments(again, option.Value), group.Key, "not " + option.Value, false, cancellationToken);
        return $"\"{option.Text}\": {on.Summary(6)}";
    }

    // Values are set through Playwright in the same browser, so the sweep does not cost a model call per value.
    private async Task<string?> SetAsync(ControlSnapshot control, string value, CancellationToken cancellationToken)
    {
        var option = control.Options.FirstOrDefault(item => item.Value == value);
        string selector;
        string action;
        switch (control.Kind)
        {
            case "select":
            case "multi-select":
                selector = control.Selector;
                action = $"await target.selectOption({Js(value)}, {{ force: true, timeout: 5000 }});";
                break;
            case "checkbox":
            case "switch":
            case "toggle":
                if (control.Selected.FirstOrDefault() == value) return null;
                selector = control.Selector;
                action = Click;
                break;
            default:
                if (option?.Selector is not { Length: > 0 } optionSelector) return "the option has no selector";
                selector = optionSelector;
                action = Click;
                break;
        }
        var code = $$"""
            const frame = page.frames()[{{control.Frame}}] || page.mainFrame();
            const target = frame.locator({{Js(selector)}}).first();
            {{action}}
            await page.waitForLoadState("networkidle", { timeout: 4000 }).catch(() => {});
            await page.waitForTimeout(300);
            return { ok: true };
            """;
        var result = await session.CallAsync("browser_run_code", new JsonObject { ["code"] = code }, cancellationToken);
        return result.IsError ? Json.Trim(result.Text, 200) : null;
    }

    private const string Click = """
        try { await target.click({ timeout: 3000 }); } catch { await target.dispatchEvent("click"); }
        """;

    private async Task<string> CollectOptionsAsync(string key, CancellationToken cancellationToken)
    {
        var control = ledger.Current?.Find(key);
        if (control is null) return "That control is not on the page now. Open it and call next_item.";
        var options = await session.CollectOptionsAsync(control.Frame, control.Selector, cancellationToken);
        ledger.Read.Add(key);
        if (options is null) return "The list could not be read. Call skip_item if it cannot be opened.";
        var record = ledger.Controls[key];
        foreach (var node in options)
        {
            var option = new OptionSnapshot(Json.Str(node?["value"]), Json.Str(node?["text"]), null, false);
            record.AllOptions.TryAdd(option.Value, option);
        }
        ledger.Collected.Add(key);
        return $"Read {options.Count} options. Close the list, then call next_item.";
    }

    private async Task<string> SignInAsync(string reason, CancellationToken cancellationToken)
    {
        if (session.LiveViewUrl is null)
        {
            ledger.Problems.Add("Signing in was needed, but the remote browser offered no live view: " + reason);
            _outcome = new PassOutcome("blocked", "Signing in was needed and no live view was available.");
            return "No live view is available, so nobody can sign in. The pass ends.";
        }
        await recorder.SetStatusAsync(RunStatus.WaitingForSignIn,
            "Sign in through the live view, return control, then press \"I have signed in\". The page asked: " + Json.Trim(reason, 200),
            session.LiveViewUrl, cancellationToken);
        var signedIn = await recorder.WaitForSignInAsync(SignInTimeout, cancellationToken);
        if (!signedIn)
        {
            ledger.Problems.Add("The admin did not sign in within 15 minutes.");
            _outcome = new PassOutcome("sign-in-timeout", "The admin did not sign in in time.");
            await recorder.SetStatusAsync(RunStatus.Running, "Nobody signed in in time.", null, cancellationToken);
            return "Nobody signed in in time. The pass ends.";
        }
        await recorder.SetStatusAsync(RunStatus.Running, "Signed in. Walking through the form.", null, cancellationToken);
        var diff = await CaptureAsync("sign-in", new JsonObject(), null, null, true, cancellationToken);
        return "The admin signed in. System: " + diff.Summary();
    }

    private string Skip(JsonObject arguments)
    {
        ledger.Skip(Json.Str(arguments["controlKey"]), Json.Str(arguments["reason"]));
        return "Skipped. Call next_item.";
    }

    private string Problem(JsonObject arguments)
    {
        var key = Json.Str(arguments["controlKey"]);
        var label = key.Length > 0 && ledger.Controls.TryGetValue(key, out var record) ? $" \"{record.Latest.Label}\"" : "";
        ledger.Problems.Add($"{Json.Str(arguments["kind"])}{label}: {Json.Str(arguments["text"])}");
        return "Recorded.";
    }

    private string Finish(JsonObject arguments)
    {
        var reason = Json.Str(arguments["reason"]);
        var pending = ledger.PendingDescriptions();
        if (reason != "blocked" && pending.Count > 0)
            return "Refused. Still unchecked on this step: " + string.Join("; ", pending.Take(10)) + ". Call next_item.";
        if (reason == "final-step-reached")
            ledger.FinalStepReached = true;
        _outcome = new PassOutcome(reason.Length > 0 ? reason : "finished", Json.Str(arguments["summary"]));
        return "Finished.";
    }

    private async Task<string> NextItemAsync(CancellationToken cancellationToken)
    {
        var item = ledger.Next();
        var kind = Json.Str(item["kind"]);
        if (kind is "continue" or "done" && ledger.Current is { } state && _visualSteps.Add(state.Step))
        {
            var found = await VisualAsync(cancellationToken);
            item = ledger.Next();
            if (found > 0)
                item["instruction"] = $"The screenshot check opened {found} control(s) that were not in the list. " + Json.Str(item["instruction"]);
        }
        return item.ToJsonString();
    }

    // A custom list or grid is not in the control list, so the screenshot is only used to choose a click. The script reads the page afterwards.
    private async Task<int> VisualAsync(CancellationToken cancellationToken)
    {
        var found = 0;
        try
        {
            await session.CallAsync("browser_run_code", new JsonObject { ["code"] = ScrollCode("top") }, cancellationToken);
            var seen = new HashSet<string>();
            var scrollY = 0;
            for (var viewport = 0; viewport < 6 && !OverBudget(out _); viewport++)
            {
                var shot = await session.ScreenshotAsync(cancellationToken);
                if (shot is null) break;
                var leftThePage = false;
                foreach (var target in (await TargetsAsync(shot, cancellationToken)).Take(3))
                {
                    if (!seen.Add($"{scrollY}:{target.X / 8}:{target.Y / 8}")) continue;
                    var before = ledger.Controls.Count;
                    var click = await session.CallAsync("browser_run_code", new JsonObject
                    {
                        ["code"] = $"await page.mouse.click({target.X}, {target.Y}); await page.waitForTimeout(400); return {{ ok: true }};"
                    }, cancellationToken);
                    if (click.IsError) continue;
                    var diff = await CaptureAsync("visual-click", new JsonObject { ["x"] = target.X, ["y"] = target.Y, ["kind"] = target.Kind }, null, null, false, cancellationToken);
                    var added = ledger.Controls.Count - before;
                    if (added > 0) found += added;
                    else ledger.Problems.Add($"A screenshot showed a {target.Kind} at {target.X},{target.Y}, but the opened page still had no readable control.");
                    await session.CallAsync("browser_run_code", new JsonObject { ["code"] = "await page.keyboard.press(\"Escape\"); await page.waitForTimeout(150); return { ok: true };" }, cancellationToken);
                    if (!diff.UrlChanged) continue;
                    leftThePage = true;
                    break;
                }
                if (leftThePage) break;
                var scroll = await session.CallAsync("browser_run_code", new JsonObject { ["code"] = ScrollCode("by") }, cancellationToken);
                var moved = Json.FirstValue(scroll.Text, item => item?["moved"] is not null);
                if (scroll.IsError || !Json.Bool(moved?["moved"])) break;
                scrollY = Number(moved?["y"]);
            }
            await session.CallAsync("browser_run_code", new JsonObject { ["code"] = ScrollCode("top") }, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ledger.Problems.Add("The screenshot check failed: " + Json.Trim(ex.Message, 200));
        }
        return found;
    }

    private async Task<List<VisualTarget>> TargetsAsync(McpImage shot, CancellationToken cancellationToken)
    {
        var state = ledger.Current;
        var known = state is null
            ? ""
            : string.Join("\n", state.Controls.Select(control => control.Label).Concat(state.Buttons.Select(button => button.Text)).Where(text => text.Length > 0).Distinct().Take(80));
        var reply = await model.LookAsync(
            "You compare a form screenshot with the controls already read from the page code.",
            "These controls are already recorded:\n" + known + """

            Point at a visible dropdown, openable list, or editable grid that is not one of those controls.
            Return only JSON: {"targets":[{"x":0,"y":0,"kind":"list"}]}
            x and y are CSS pixels in the image, origin at the top-left. kind is "list" or "grid".
            At most 3 targets. If nothing is missing, return {"targets":[]}.
            Never point at a button that submits, pays, buys, or sends the form.
            """,
            shot.MimeType.Length > 0 ? shot.MimeType : "image/jpeg",
            shot.Base64,
            cancellationToken);
        var node = Json.FirstValue(reply, item => item?["targets"] is JsonArray);
        var targets = new List<VisualTarget>();
        foreach (var target in Json.Array(node?["targets"]))
        {
            var x = Number(target?["x"]);
            var y = Number(target?["y"]);
            if (x < 0 || y < 0 || x > 4000 || y > 4000) continue;
            var kind = Json.Str(target?["kind"]);
            targets.Add(new VisualTarget(x, y, kind is "grid" ? "grid" : "list"));
        }
        return targets;
    }

    private static string ScrollCode(string action) =>
        "return await page.evaluate(() => {" +
        "const doc = document.scrollingElement || document.documentElement;" +
        "let node = doc; let delta = doc.scrollHeight - doc.clientHeight;" +
        "for (const item of document.querySelectorAll('body *')) {" +
        "const style = getComputedStyle(item);" +
        "if (!/(auto|scroll|overlay)/.test(style.overflowY)) continue;" +
        "const range = item.scrollHeight - item.clientHeight;" +
        "if (range > delta && item.clientHeight > 80) { node = item; delta = range; }" +
        "}" +
        "const useWindow = node === doc || node === document.body;" +
        (action == "top"
            ? "if (useWindow) window.scrollTo(0, 0); else node.scrollTop = 0; return { ok: true };"
            : "const before = useWindow ? window.scrollY : node.scrollTop;" +
              "if (useWindow) window.scrollBy(0, Math.round((useWindow ? window.innerHeight : node.clientHeight) * 0.8));" +
              "else node.scrollTop = before + Math.round(node.clientHeight * 0.8);" +
              "const after = useWindow ? window.scrollY : node.scrollTop;" +
              "return { moved: after !== before, y: after };") +
        "});";

    private static int Number(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue<double>(out var number)) return (int)Math.Round(number);
        return Json.Int(node);
    }

    private async Task<StateDiff> CaptureAsync(string tool, JsonObject arguments, string? controlKey, string? value, bool movesOn, CancellationToken cancellationToken)
    {
        JsonNode raw;
        try
        {
            raw = await session.InventoryAsync(3000, cancellationToken);
        }
        catch (McpException)
        {
            raw = await session.InventoryAsync(300, cancellationToken);
        }
        var state = Inventory.Parse(raw);
        var diff = ledger.Record(state, tool, arguments, controlKey, value, movesOn);
        var screenshot = _fingerprints.Add(state.Fingerprint) ? await session.ScreenshotAsync(cancellationToken) : null;
        await recorder.OnStateAsync(state, screenshot, cancellationToken);
        await recorder.CountsAsync(ledger.Actions.Count, ledger.States.Count, cancellationToken);
        return diff;
    }

    private (string? ControlKey, string? Value, bool PressedButton) Attribute(string tool, JsonObject arguments)
    {
        var state = ledger.Current;
        var target = Json.Str(arguments["target"]);
        if (state is null || target.Length == 0) return (null, null, false);
        foreach (var control in state.Controls)
        {
            if (tool == "browser_select_option" && control.Selector == target)
                return (control.Key, Json.Str(Json.Array(arguments["values"]).FirstOrDefault()), false);
            if (tool == "browser_type" && control.Selector == target)
                return (control.Key, "filled", false);
            if (tool != "browser_click") continue;
            if (control.Selector == target && control.Kind is "checkbox" or "switch" or "toggle")
                return (control.Key, control.Selected.FirstOrDefault() == "true" ? "false" : "true", false);
            var option = control.Options.FirstOrDefault(item => item.Selector == target);
            if (option is not null)
                return (control.Key, option.Value, false);
        }
        var pressed = tool == "browser_click" && state.Buttons.Any(button => button.Selector == target);
        return (null, null, pressed || tool == "browser_click" && state.Controls.All(control => control.Selector != target));
    }

    private bool OverBudget(out string reason)
    {
        if (ledger.Actions.Count >= settings.MaxActions)
        {
            reason = $"The run reached its budget of {settings.MaxActions} actions.";
            return true;
        }
        if (_clock.Elapsed >= settings.MaxDuration)
        {
            reason = $"The run reached its budget of {settings.MaxDuration.TotalMinutes:0} minutes.";
            return true;
        }
        reason = "";
        return false;
    }

    private static List<OptionSnapshot> Sample(List<OptionSnapshot> options, int count)
    {
        if (options.Count == 0 || count <= 0) return [];
        if (count == 1) return [options[0]];
        if (count >= options.Count) return options;
        var picked = new List<OptionSnapshot>();
        var step = (double)(options.Count - 1) / (count - 1);
        for (var index = 0; index < count; index++)
        {
            var option = options[(int)Math.Round(index * step)];
            if (!picked.Contains(option)) picked.Add(option);
        }
        return picked;
    }

    private static JsonObject SetArguments(ControlSnapshot control, string value) => new()
    {
        ["frame"] = control.Frame,
        ["kind"] = control.Kind,
        ["selector"] = control.Selector,
        ["value"] = value,
        ["optionSelector"] = control.Options.FirstOrDefault(option => option.Value == value)?.Selector
    };

    private static string Js(string text) => JsonValue.Create(text).ToJsonString();

    private static List<ChatItem> Window(List<ChatItem> history, int turns = 10)
    {
        var assistant = history.Select((item, index) => (item, index)).Where(pair => pair.item is AssistantTurn).Select(pair => pair.index).ToList();
        if (assistant.Count <= turns) return history;
        var window = new List<ChatItem>
        {
            history[0],
            new UserText("Earlier turns are omitted. The system keeps the record of the pass; call next_item when unsure.")
        };
        window.AddRange(history.Skip(assistant[^turns]));
        return window;
    }

    private List<ToolSpec> BuildTools()
    {
        var tools = new List<ToolSpec>();
        foreach (var node in session.Tools)
        {
            var name = Json.Str(node?["name"]);
            if (!BrowserTools.Contains(name)) continue;
            var schema = node?["inputSchema"]?.DeepClone() as JsonObject ?? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
            (schema["properties"] as JsonObject)?.Remove("browserSessionId");
            if (schema["required"] is JsonArray required)
                schema["required"] = new JsonArray(required.Select(Json.Str).Where(item => item != "browserSessionId").Select(item => (JsonNode?)item).ToArray());
            tools.Add(new ToolSpec(name, Json.Str(node?["description"]), schema));
        }
        tools.Add(Tool("next_item", "Returns the next thing to check on the page: the control, its key and selector, and an instruction."));
        tools.Add(Tool("try_all_options", "Sets every untried value of a control one by one and records what each value shows, hides, or changes. Returns one line per value.",
            ("controlKey", "string", "The key from next_item.", null)));
        tools.Add(Tool("collect_options", "Scrolls an open list to its end and records all of its options.",
            ("controlKey", "string", "The key from next_item.", null)));
        tools.Add(Tool("skip_item", "Marks a control as not checkable. The report lists it with the reason.",
            ("controlKey", "string", "The key from next_item.", null),
            ("reason", "string", "Why it cannot be checked.", null)));
        tools.Add(Tool("request_sign_in", "Pauses the pass until the admin signs in through the live view.",
            ("reason", "string", "What the page asks for.", null)));
        tools.Add(Tool("report_problem", "Records a problem for the report.",
            ("kind", "string", "The kind of problem.", ["rejected-value", "blocked", "captcha", "other"]),
            ("text", "string", "What happened.", null),
            ("controlKey", "string", "The key of the control, when the problem concerns one.", null)));
        tools.Add(Tool("finish", "Ends the pass. Refused while items are left on the step, unless the reason is blocked.",
            ("reason", "string", "Why the pass ends.", ["final-step-reached", "blocked"]),
            ("summary", "string", "One sentence for the admin.", null)));
        return tools;
    }

    private static ToolSpec Tool(string name, string description, params (string Name, string Type, string Description, string[]? Values)[] parameters)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var (parameter, type, text, values) in parameters)
        {
            var property = new JsonObject { ["type"] = type, ["description"] = text };
            if (values is not null) property["enum"] = new JsonArray(values.Select(value => (JsonNode?)value).ToArray());
            properties[parameter] = property;
            if (parameter != "controlKey" || name != "report_problem") required.Add(parameter);
        }
        return new ToolSpec(name, description, new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = required });
    }

    private string SystemPrompt() => $$"""
        You are the interpretation agent of eisenberg. You walk through one web form in a remote browser so that the system can record every control, every option, and what each choice shows or hides. The system builds the data model from that record. You do not write the model.

        The start address: {{address}}
        The admin's description of the form:
        <description>
        {{(description.Length > 0 ? description : "(none)")}}
        </description>

        Rules:
        - Work in a loop: call next_item, do what it says, call next_item again.
        - After every browser action the system lists the controls itself and tells you what appeared, disappeared, or changed. You do not report controls.
        - try_all_options sets every value of a control for you and records what each value changes. Use it whenever next_item asks for it.
        - Use the selector from next_item as the target of browser actions when one is given. Use browser_snapshot when you need something else, such as a menu or a button.
        - To move to the next step, fill every required field with a plausible test value and press the button that continues. Test values must pass validation in the form's country, for example a valid Polish PESEL or postcode on a Polish form. Never use real personal data. Use the test values from the description when there are any.
        - Never press the final submit: the button that sends an application, buys, pays, orders, or confirms. The description may name more buttons that must not be pressed. When only the final submit is left, call finish with reason final-step-reached.
        - If the site asks you to sign in, call request_sign_in. The admin signs in through the live view and the pass continues.
        - If the site rejects a value and you find no value it accepts, call report_problem with kind rejected-value and the control key, then continue, or finish with reason blocked.
        - If a CAPTCHA or a block page appears, call report_problem with kind captcha and finish with reason blocked.
        - Close cookie banners, accepting only what is needed to continue.
        - If an item cannot be done, call skip_item with the reason instead of retrying it.
        - Keep your messages short.
        """;
}
