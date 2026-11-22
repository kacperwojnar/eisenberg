using System.Diagnostics;
using System.Text.Json.Nodes;
using Eisenberg.Data;
using Microsoft.EntityFrameworkCore;

namespace Eisenberg.Worker.Interpretation;

sealed class Interpreter(IServiceProvider services, IConfiguration configuration, IHttpClientFactory httpFactory, ILogger<Interpreter> logger)
{
    public async Task RunAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EisenbergDb>();
        var run = await db.InterpretationRuns.Include(r => r.Form).SingleOrDefaultAsync(r => r.Id == runId, cancellationToken);
        // Claimed by InterpretationConsumer (queued -> running) before this method runs.
        if (run is null || run.Status != RunStatus.Running)
            return;

        var missing = new[] { "PLAYWRIGHT_MCP_URL", "PLAYWRIGHT_MCP_API_KEY", "FOUNDRY_ENDPOINT", "FOUNDRY_API_KEY", "FOUNDRY_AGENT_DEPLOYMENT" }
            .Where(name => string.IsNullOrWhiteSpace(configuration[name])).ToList();
        if (missing.Count > 0)
        {
            run.Status = RunStatus.NotConnected;
            run.StatusText = "Not connected. Empty settings: " + string.Join(", ", missing) + ".";
            run.FinishedAt = DateTimeOffset.UtcNow;
            run.ReportJson = new JsonObject { ["stoppedBecause"] = "not-connected", ["missingSettings"] = new JsonArray(missing.Select(name => (JsonNode?)name).ToArray()) }.ToJsonString();
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        var settings = new PassSettings(
            Number("INTERPRETATION_MAX_ACTIONS", 400),
            TimeSpan.FromMinutes(Number("INTERPRETATION_MAX_MINUTES", 60)),
            Number("INTERPRETATION_MAX_SWEEP_OPTIONS", 60));
        var endpoint = configuration["FOUNDRY_ENDPOINT"]!;
        var apiKey = configuration["FOUNDRY_API_KEY"]!;
        var builderDeployment = string.IsNullOrWhiteSpace(configuration["FOUNDRY_MODEL_DEPLOYMENT"]) ? "claude-sonnet-5-5" : configuration["FOUNDRY_MODEL_DEPLOYMENT"]!;
        var http = httpFactory.CreateClient("interpretation");
        var agent = ChatModels.Create(http, endpoint, apiKey, configuration["FOUNDRY_AGENT_DEPLOYMENT"]!);
        var builder = ChatModels.Create(http, endpoint, apiKey, builderDeployment);
        var recorder = new RunRecorder(db, run, StorageRoot());
        var ledger = new Ledger();
        var clock = Stopwatch.StartNew();
        BrowserSession? session = null;
        PassOutcome? outcome = null;
        BuiltModel? built = null;

        try
        {
            await recorder.SetStatusAsync(RunStatus.Running, "Opening the remote browser.", null, cancellationToken);
            var mcp = new McpClient(http, new Uri(configuration["PLAYWRIGHT_MCP_URL"]!), configuration["PLAYWRIGHT_MCP_API_KEY"]!);
            session = await BrowserSession.OpenAsync(mcp, cancellationToken);
            await recorder.SetStatusAsync(RunStatus.Running, "Walking through the form.", session.LiveViewUrl, cancellationToken);

            outcome = await new AgentPass(session, agent, ledger, recorder, settings, run.Form.Address, run.Description, logger).RunAsync(cancellationToken);
            await recorder.SaveActionsAsync(ledger, cancellationToken);
            await session.CloseAsync();
            session = null;

            await recorder.SetStatusAsync(RunStatus.BuildingModel, "Building the model.", null, cancellationToken);
            built = await new ModelBuilder(builder).BuildAsync(ledger, run.Description, run.Form.Address, cancellationToken);

            await recorder.SetStatusAsync(RunStatus.Checking, "Checking the result.", null, cancellationToken);
            var modelId = Guid.NewGuid();
            var lists = built.ChoiceLists.Select(list => new ChoiceList
            {
                Id = Guid.NewGuid(),
                ModelId = modelId,
                ControlKey = list.ControlKey,
                IsComplete = list.IsComplete,
                DependsOnJson = list.DependsOn.ToJsonString(),
                ChoicesJson = list.Choices.ToJsonString()
            }).ToList();
            var number = (await db.ModelVersions.Where(m => m.FormId == run.FormId).MaxAsync(m => (int?)m.Number, cancellationToken) ?? 0) + 1;
            db.ModelVersions.Add(new FormModelVersion
            {
                Id = modelId,
                FormId = run.FormId,
                RunId = run.Id,
                Number = number,
                Status = ModelStatus.Draft,
                ModelJson = built.WithChoiceListIds(lists.ToDictionary(list => list.ControlKey, list => list.Id)).ToJsonString(),
                CreatedAt = DateTimeOffset.UtcNow
            });
            db.ChoiceLists.AddRange(lists);
            run.ReportJson = Report.Build(ledger, outcome, built, agent, builder, clock.Elapsed, null).ToJsonString();
            run.FinishedAt = DateTimeOffset.UtcNow;
            if (outcome?.StoppedBecause is "blocked" or "sign-in-timeout")
            {
                run.Status = RunStatus.Failed;
                run.StatusText = $"Stopped ({outcome.StoppedBecause}). Draft model {number} was still saved for review.";
            }
            else
            {
                run.Status = RunStatus.Finished;
                run.StatusText = outcome?.StoppedBecause == "final-step-reached"
                    ? $"Finished. Draft model {number} is ready for review."
                    : $"Finished with gaps ({outcome?.StoppedBecause}). Draft model {number} is ready for review.";
            }
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Interpretation {RunId} failed", runId);
            db.ChangeTracker.Clear();
            var failed = await db.InterpretationRuns.SingleAsync(r => r.Id == runId, CancellationToken.None);
            failed.Status = RunStatus.Failed;
            failed.StatusText = "Failed: " + Json.Trim(ex.Message, 300);
            failed.FinishedAt = DateTimeOffset.UtcNow;
            failed.ReportJson = Report.Build(ledger, outcome, built, agent, builder, clock.Elapsed, ex.Message).ToJsonString();
            await db.SaveChangesAsync(CancellationToken.None);
        }
        finally
        {
            if (session is not null)
                await session.CloseAsync();
        }
    }

    private int Number(string name, int fallback) =>
        int.TryParse(configuration[name], out var value) && value > 0 ? value : fallback;

    private string StorageRoot() =>
        configuration["FILE_STORAGE_ROOT"] is { Length: > 0 } root
            ? root
            : Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "data", "files"));
}

sealed class RunRecorder(EisenbergDb db, InterpretationRun run, string storageRoot) : IRunRecorder
{
    private int _sequence;

    public async Task OnStateAsync(PageState state, McpImage? screenshot, CancellationToken cancellationToken)
    {
        if (screenshot is null) return;
        var directory = Path.Combine(storageRoot, "runs", run.Id.ToString());
        Directory.CreateDirectory(directory);
        var sequence = ++_sequence;
        var caption = $"State {state.Index + 1}, step {state.Step + 1}: {state.Headings.LastOrDefault() ?? state.Title}";
        var extension = screenshot.MimeType.Contains("png", StringComparison.OrdinalIgnoreCase) ? "png" : "jpg";
        var image = Path.Combine(directory, $"{sequence:D4}.{extension}");
        await File.WriteAllBytesAsync(image, Convert.FromBase64String(screenshot.Base64), cancellationToken);
        var inventory = Path.Combine(directory, $"{sequence:D4}.json");
        await File.WriteAllTextAsync(inventory, state.Raw.ToJsonString(), cancellationToken);
        db.RunArtifacts.Add(Artifact(sequence, "screenshot", caption, image, screenshot.MimeType.Length > 0 ? screenshot.MimeType : "image/jpeg"));
        db.RunArtifacts.Add(Artifact(sequence, "inventory", caption, inventory, "application/json"));
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SaveActionsAsync(Ledger ledger, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(storageRoot, "runs", run.Id.ToString());
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "actions.json");
        var actions = new JsonArray(ledger.Actions.Select(action => (JsonNode?)new JsonObject
        {
            ["sequence"] = action.Sequence,
            ["tool"] = action.Tool,
            ["arguments"] = action.Arguments.DeepClone(),
            ["controlKey"] = action.ControlKey,
            ["value"] = action.Value,
            ["stateBefore"] = action.StateBefore,
            ["stateAfter"] = action.StateAfter
        }).ToArray());
        await File.WriteAllTextAsync(path, actions.ToJsonString(), cancellationToken);
        db.RunArtifacts.Add(Artifact(++_sequence, "actions", "Recorded actions", path, "application/json"));
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetStatusAsync(string status, string text, string? liveViewUrl, CancellationToken cancellationToken)
    {
        run.Status = status;
        run.StatusText = text;
        if (liveViewUrl is not null) run.LiveViewUrl = liveViewUrl;
        if (status == RunStatus.WaitingForSignIn) run.SignInConfirmed = false;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> WaitForSignInAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            await db.Entry(run).ReloadAsync(cancellationToken);
            if (run.SignInConfirmed) return true;
        }
        return false;
    }

    public async Task CountsAsync(int actions, int states, CancellationToken cancellationToken)
    {
        run.ActionCount = actions;
        run.StateCount = states;
        await db.SaveChangesAsync(cancellationToken);
    }

    private RunArtifact Artifact(int sequence, string kind, string caption, string path, string contentType) => new()
    {
        Id = Guid.NewGuid(),
        RunId = run.Id,
        Sequence = sequence,
        Kind = kind,
        Caption = caption,
        StorageLocation = path,
        ContentType = contentType,
        CreatedAt = DateTimeOffset.UtcNow
    };
}
