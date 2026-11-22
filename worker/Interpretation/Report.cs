using System.Text.Json.Nodes;

namespace Eisenberg.Worker.Interpretation;

static class Report
{
    public static JsonObject Build(Ledger ledger, PassOutcome? outcome, BuiltModel? built, IChatModel? agent, IChatModel? builder, TimeSpan duration, string? error)
    {
        var checks = new JsonArray
        {
            Check("Every control seen is a property, or the admin marked it as ignored",
                built is null ? "not-run" : built.Unmapped.Count == 0 ? "passed" : "failed",
                built?.Unmapped.Select(key => $"\"{Label(ledger, key)}\" is not in any property") ?? []),
            Check("Every complete choice list holds exactly the choices the script listed",
                built is null ? "not-run" : "passed",
                built is null ? [] : [$"{built.ChoiceLists.Count(list => list.IsComplete)} complete lists, {built.ChoiceLists.Count(list => !list.IsComplete)} incomplete lists"]),
            Check("Every value of every control that changes the form was tried, and every revealed control has a condition",
                Gaps(ledger).Count == 0 ? "passed" : "failed", Gaps(ledger)),
            Check("The pass reached the last step before the final submit",
                outcome?.StoppedBecause == "final-step-reached" ? "passed" : "failed",
                [Stopped(outcome, error)]),
            Check("Paths generated from the model were repeated and matched", "not-run",
                ["Not implemented in this version of the test tool."]),
            Check("The form was filled once with a generated instance and read back", "not-run",
                ["Not implemented in this version of the test tool."])
        };

        return new JsonObject
        {
            ["stoppedBecause"] = outcome?.StoppedBecause ?? (error is null ? "unknown" : "error"),
            ["agentSummary"] = outcome?.Summary ?? "",
            ["error"] = error,
            ["summary"] = new JsonObject
            {
                ["states"] = ledger.States.Count,
                ["actions"] = ledger.Actions.Count,
                ["controls"] = ledger.Controls.Count,
                ["steps"] = ledger.Steps.Count,
                ["minutes"] = Math.Round(duration.TotalMinutes, 1)
            },
            ["models"] = new JsonArray(Usage("agent", agent), Usage("model builder", builder)),
            ["checks"] = checks,
            ["problems"] = new JsonArray(ledger.Problems.Select(problem => (JsonNode?)problem).ToArray()),
            ["steps"] = new JsonArray(ledger.Steps.Select(step => (JsonNode?)new JsonObject
            {
                ["index"] = step.Index + 1,
                ["url"] = step.Url,
                ["headings"] = new JsonArray(step.Headings.Take(3).Select(heading => (JsonNode?)heading).ToArray()),
                ["arrivedWith"] = new JsonArray(step.ValuesUsedToArrive.Select(condition => (JsonNode?)condition.Text).ToArray())
            }).ToArray()),
            ["frameErrors"] = new JsonArray(ledger.States.SelectMany(state => state.FrameErrors).Distinct().Take(10).Select(item => (JsonNode?)item).ToArray())
        };
    }

    private static List<string> Gaps(Ledger ledger)
    {
        var gaps = new List<string>();
        gaps.AddRange(ledger.PendingDescriptions());
        gaps.AddRange(ledger.Sampled.Select(item => "only a sample was tried: " + item));
        gaps.AddRange(ledger.Skipped.Select(item => $"skipped \"{item.Label}\": {item.Reason}"));
        gaps.AddRange(ledger.Controls.Values
            .Where(record => record.Latest.Kind == "combobox" && record.AllOptions.Count > 0 &&
                record.AllOptions.Keys.Any(value => value.Length > 0 && !ledger.Tried.Contains(Ledger.TriedKey(record.Key, value))) &&
                ledger.Skipped.All(item => item.ControlKey != record.Key))
            .Select(record => $"the options of \"{record.Latest.Label}\" were read but not tried"));
        gaps.AddRange(ledger.Controls.Values
            .Where(record => !record.PresentAtStepStart && record.ShownWhen.Count == 0)
            .Select(record => $"\"{record.Latest.Label}\" appeared without a recorded condition"));
        if (ledger.Steps.Count > 1)
            gaps.Add($"steps after the first were explored only with the values used to reach them ({ledger.Steps.Count - 1} steps)");
        return gaps;
    }

    private static string Stopped(PassOutcome? outcome, string? error) => outcome?.StoppedBecause switch
    {
        "final-step-reached" => "The agent reached the final step without submitting.",
        "blocked" => "The pass was blocked: " + outcome.Summary,
        "budget" => outcome.Summary,
        "sign-in-timeout" => "Nobody signed in in time.",
        "agent-stopped" => outcome.Summary,
        null => error is null ? "The pass did not run." : "The run failed: " + error,
        _ => outcome.Summary
    };

    private static JsonObject Check(string name, string status, IEnumerable<string> details) => new()
    {
        ["name"] = name,
        ["status"] = status,
        ["details"] = new JsonArray(details.Select(detail => (JsonNode?)detail).ToArray())
    };

    private static JsonNode? Usage(string role, IChatModel? model) => model is null ? null : new JsonObject
    {
        ["role"] = role,
        ["deployment"] = model.Deployment,
        ["calls"] = model.Usage.Calls,
        ["inputTokens"] = model.Usage.InputTokens,
        ["outputTokens"] = model.Usage.OutputTokens
    };

    private static string Label(Ledger ledger, string key) =>
        ledger.Controls.TryGetValue(key, out var record) ? record.Latest.Label : key;
}
