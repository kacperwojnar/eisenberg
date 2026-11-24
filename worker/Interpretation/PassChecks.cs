using System.Text.Json.Nodes;

namespace Eisenberg.Worker.Interpretation;

static class ActionReplay
{
    public static async Task ReplayAsync(BrowserSession session, string address, IReadOnlyList<RecordedAction> actions, CancellationToken cancellationToken)
    {
        await session.CallAsync("browser_navigate", new JsonObject { ["url"] = address }, cancellationToken);
        foreach (var action in actions.Take(40))
        {
            if (action.Tool.StartsWith("browser_", StringComparison.Ordinal))
            {
                await session.CallAsync(action.Tool, (JsonObject)action.Arguments.DeepClone(), cancellationToken);
            }
            else if (action.Tool == "set-value")
            {
                var frame = Json.Int(action.Arguments["frame"]);
                var selector = Json.Str(action.Arguments["selector"]);
                var value = Json.Str(action.Arguments["value"]);
                var kind = Json.Str(action.Arguments["kind"]);
                var option = Json.Str(action.Arguments["optionSelector"]);
                var code = kind is "select" or "multi-select"
                    ? $"const frame = page.frames()[{frame}] || page.mainFrame(); await frame.locator({Js(selector)}).first().selectOption({Js(value)}, {{ force: true, timeout: 5000 }}); return {{ ok: true }};"
                    : $"const frame = page.frames()[{frame}] || page.mainFrame(); await frame.locator({Js(option.Length > 0 ? option : selector)}).first().click({{ timeout: 3000 }}); return {{ ok: true }};";
                await session.CallAsync("browser_run_code", new JsonObject { ["code"] = code }, cancellationToken);
            }
            else if (action.Tool == "visual-click")
            {
                var code = $"await page.mouse.click({Json.Int(action.Arguments["x"])}, {Json.Int(action.Arguments["y"])}); return {{ ok: true }};";
                await session.CallAsync("browser_run_code", new JsonObject { ["code"] = code }, cancellationToken);
            }
        }
    }

    private static string Js(string text) => JsonValue.Create(text)!.ToJsonString();
}

static class PassChecks
{
    public static async Task<(JsonObject Path, JsonObject Fill)> RunAsync(BrowserSession session, Ledger ledger, BuiltModel built, string address, CancellationToken cancellationToken)
    {
        return (await PathAsync(session, ledger, address, cancellationToken), await FillAsync(session, built, cancellationToken));
    }

    private static async Task<JsonObject> PathAsync(BrowserSession session, Ledger ledger, string address, CancellationToken cancellationToken)
    {
        if (ledger.Actions.Count == 0)
            return Check("not-run", ["The pass recorded no actions to repeat."]);
        try
        {
            await ActionReplay.ReplayAsync(session, address, ledger.Actions, cancellationToken);
            var raw = await session.InventoryAsync(3000, cancellationToken);
            var state = Inventory.Parse(raw);
            var expected = ledger.Current?.Controls.Select(control => control.Key).ToHashSet() ?? [];
            var seen = state.Controls.Select(control => control.Key).ToHashSet();
            var missing = expected.Except(seen).Take(12).ToList();
            var status = expected.Count == 0 || missing.Count * 2 <= expected.Count ? "passed" : "failed";
            return Check(status, missing.Count == 0 ? ["The repeated path showed the recorded controls."] : missing.Select(key => $"missing after replay: {key}"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Check("failed", [Json.Trim(ex.Message, 300)]);
        }
    }

    private static async Task<JsonObject> FillAsync(BrowserSession session, BuiltModel built, CancellationToken cancellationToken)
    {
        var properties = Json.Array(built.Model["types"]).SelectMany(type => Json.Array(type?["properties"])).Take(12).ToList();
        if (properties.Count == 0)
            return Check("not-run", ["The model has no properties to fill."]);
        var details = new List<string>();
        var found = 0;
        foreach (var property in properties)
        {
            var name = Json.Str(property?["name"]);
            var control = Json.Array(property?["controls"]).FirstOrDefault();
            var selector = Json.Str(control?["selector"]);
            var frame = Json.Int(control?["frame"]);
            if (selector.Length == 0)
            {
                details.Add($"{name}: no selector");
                continue;
            }
            var code = $"const frame = page.frames()[{frame}] || page.mainFrame(); const target = frame.locator({JsonValue.Create(selector)}).first(); const tag = await target.evaluate(element => element.tagName); if (tag === \"SELECT\") await target.selectOption({{ index: 1 }}).catch(async () => target.selectOption({{ index: 0 }})); else await target.fill(\"test\"); return {{ ok: true }};";
            var result = await session.CallAsync("browser_run_code", new JsonObject { ["code"] = code }, cancellationToken);
            if (result.IsError) details.Add($"{name}: not set");
            else
            {
                found++;
                details.Add($"{name}: set");
            }
        }
        var read = await session.InventoryAsync(300, cancellationToken);
        var state = Inventory.Parse(read);
        var withValue = state.Controls.Count(control => control.HasValue);
        var status = found > 0 && withValue > 0 ? "passed" : "failed";
        details.Add($"{withValue} controls had a value when read back.");
        return Check(status, details);
    }

    private static JsonObject Check(string status, IEnumerable<string> details) => new()
    {
        ["status"] = status,
        ["details"] = new JsonArray(details.Select(detail => (JsonNode?)detail).ToArray())
    };
}
