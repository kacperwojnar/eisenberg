using System.Text.Json;
using Eisenberg.Data;
using Microsoft.EntityFrameworkCore;

static class FormEndpoints
{
    public static void MapForms(this WebApplication app)
    {
        app.MapGet("/admin/forms", async (HttpContext http, EisenbergDb db) =>
        {
            if (!IsAdmin(http)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var forms = await db.Forms.Include(f => f.Publications).OrderByDescending(f => f.CreatedAt).ToListAsync();
            var formIds = forms.Select(f => f.Id).ToArray();
            var runs = await db.InterpretationRuns.Where(r => formIds.Contains(r.FormId))
                .OrderByDescending(r => r.CreatedAt).ToListAsync();
            var published = await db.ModelVersions.Where(m => formIds.Contains(m.FormId) && m.Status == ModelStatus.Published)
                .Select(m => new { m.Id, m.Number }).ToListAsync();
            return Results.Json(forms.Select(f =>
            {
                var last = runs.FirstOrDefault(r => r.FormId == f.Id);
                return new
                {
                    f.Id,
                    f.Address,
                    f.Description,
                    f.CreatedAt,
                    f.PublishedModelId,
                    publishedModelNumber = published.FirstOrDefault(m => m.Id == f.PublishedModelId)?.Number,
                    organizationIds = f.Publications.Select(p => p.OrganizationId),
                    lastRun = last is null ? null : new { last.Id, last.Status, last.StatusText, last.CreatedAt }
                };
            }));
        });

        app.MapPost("/admin/forms", async (FormBody body, HttpContext http, EisenbergDb db, QueuePublisher publisher) =>
        {
            if (!IsAdmin(http)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (!publisher.IsConnected)
                return Results.Problem("RABBITMQ_URL is required.", statusCode: StatusCodes.Status503ServiceUnavailable);
            if (!Uri.TryCreate(body.Address?.Trim(), UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https"))
                return Results.BadRequest("The address must be an http or https URL.");
            var form = new FormDefinition
            {
                Id = Guid.NewGuid(),
                Address = address.ToString(),
                Description = body.Description?.Trim() ?? "",
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.Forms.Add(form);
            var run = NewRun(form);
            db.InterpretationRuns.Add(run);
            await db.SaveChangesAsync();
            publisher.PublishInterpretationRequested(run.Id);
            return Results.Json(new { form.Id, runId = run.Id });
        });

        app.MapGet("/admin/forms/{id:guid}", async (Guid id, HttpContext http, EisenbergDb db) =>
        {
            if (!IsAdmin(http)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var form = await db.Forms.Include(f => f.Publications).SingleOrDefaultAsync(f => f.Id == id);
            if (form is null) return Results.NotFound();
            var runs = await db.InterpretationRuns.Where(r => r.FormId == id).OrderByDescending(r => r.CreatedAt)
                .Select(r => new { r.Id, r.Status, r.StatusText, r.CreatedAt, r.FinishedAt, r.ActionCount, r.StateCount })
                .ToListAsync();
            var models = await db.ModelVersions.Where(m => m.FormId == id).OrderByDescending(m => m.Number)
                .Select(m => new { m.Id, m.Number, m.Status, m.RunId, m.CreatedAt, m.PublishedAt })
                .ToListAsync();
            return Results.Json(new
            {
                form.Id,
                form.Address,
                form.Description,
                form.CreatedAt,
                form.PublishedModelId,
                organizationIds = form.Publications.Select(p => p.OrganizationId),
                runs,
                models
            });
        });

        app.MapPut("/admin/forms/{id:guid}", async (Guid id, DescriptionBody body, HttpContext http, EisenbergDb db) =>
        {
            if (!IsAdmin(http)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var form = await db.Forms.SingleOrDefaultAsync(f => f.Id == id);
            if (form is null) return Results.NotFound();
            form.Description = body.Description?.Trim() ?? "";
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        app.MapPost("/admin/forms/{id:guid}/runs", async (Guid id, HttpContext http, EisenbergDb db, QueuePublisher publisher) =>
        {
            if (!IsAdmin(http)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (!publisher.IsConnected)
                return Results.Problem("RABBITMQ_URL is required.", statusCode: StatusCodes.Status503ServiceUnavailable);
            var form = await db.Forms.SingleOrDefaultAsync(f => f.Id == id);
            if (form is null) return Results.NotFound();
            if (await db.InterpretationRuns.AnyAsync(r => r.FormId == id &&
                    (r.Status == RunStatus.Queued || r.Status == RunStatus.Running || r.Status == RunStatus.WaitingForSignIn ||
                     r.Status == RunStatus.BuildingModel || r.Status == RunStatus.Checking)))
                return Results.Conflict("A run for this form is still in progress.");
            var run = NewRun(form);
            db.InterpretationRuns.Add(run);
            await db.SaveChangesAsync();
            publisher.PublishInterpretationRequested(run.Id);
            return Results.Json(new { runId = run.Id });
        });

        app.MapGet("/admin/runs/{id:guid}", async (Guid id, HttpContext http, EisenbergDb db) =>
        {
            if (!IsAdmin(http)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var run = await db.InterpretationRuns.SingleOrDefaultAsync(r => r.Id == id);
            if (run is null) return Results.NotFound();
            var artifacts = await db.RunArtifacts.Where(a => a.RunId == id).OrderBy(a => a.Sequence)
                .Select(a => new { a.Id, a.Sequence, a.Kind, a.Caption, a.ContentType })
                .ToListAsync();
            var modelId = await db.ModelVersions.Where(m => m.RunId == id).Select(m => (Guid?)m.Id).FirstOrDefaultAsync();
            return Results.Json(new
            {
                run.Id,
                run.FormId,
                run.Status,
                run.StatusText,
                run.LiveViewUrl,
                run.SignInConfirmed,
                run.Description,
                run.CreatedAt,
                run.FinishedAt,
                run.ActionCount,
                run.StateCount,
                report = ParseOrNull(run.ReportJson),
                modelId,
                artifacts
            });
        });

        app.MapPost("/admin/runs/{id:guid}/signed-in", async (Guid id, HttpContext http, EisenbergDb db) =>
        {
            if (!IsAdmin(http)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var run = await db.InterpretationRuns.SingleOrDefaultAsync(r => r.Id == id);
            if (run is null) return Results.NotFound();
            if (run.Status != RunStatus.WaitingForSignIn) return Results.Conflict("The run is not waiting for signing in.");
            run.SignInConfirmed = true;
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        app.MapGet("/admin/artifacts/{id:guid}", async (Guid id, HttpContext http, EisenbergDb db, FileStore store) =>
        {
            if (!IsAdmin(http)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var artifact = await db.RunArtifacts.SingleOrDefaultAsync(a => a.Id == id);
            if (artifact is null || !store.IsUnderRoot(artifact.StorageLocation) || !File.Exists(artifact.StorageLocation))
                return Results.NotFound();
            return Results.File(artifact.StorageLocation, artifact.ContentType);
        });

        app.MapGet("/admin/models/{id:guid}", async (Guid id, HttpContext http, EisenbergDb db) =>
        {
            if (!IsAdmin(http)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var model = await db.ModelVersions.SingleOrDefaultAsync(m => m.Id == id);
            if (model is null) return Results.NotFound();
            var lists = await db.ChoiceLists.Where(c => c.ModelId == id).ToListAsync();
            return Results.Json(new
            {
                model.Id,
                model.FormId,
                model.RunId,
                model.Number,
                model.Status,
                model.CreatedAt,
                model.PublishedAt,
                model = ParseOrNull(model.ModelJson),
                choiceLists = lists.Select(c => new
                {
                    c.Id,
                    c.ControlKey,
                    c.IsComplete,
                    dependsOn = ParseOrNull(c.DependsOnJson),
                    choices = ParseOrNull(c.ChoicesJson)
                })
            });
        });

        app.MapPut("/admin/models/{id:guid}", async (Guid id, ModelBody body, HttpContext http, EisenbergDb db) =>
        {
            if (!IsAdmin(http)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var model = await db.ModelVersions.SingleOrDefaultAsync(m => m.Id == id);
            if (model is null) return Results.NotFound();
            if (model.Status != ModelStatus.Draft) return Results.Conflict("Only a draft can be edited. Start a new run for changes.");
            if (body.Model.ValueKind != JsonValueKind.Object ||
                !body.Model.TryGetProperty("types", out var types) || types.ValueKind != JsonValueKind.Array)
                return Results.BadRequest("The model must be an object with a types array.");
            model.ModelJson = body.Model.GetRawText();
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        app.MapPost("/admin/models/{id:guid}/publish", async (Guid id, PublishBody body, HttpContext http, EisenbergDb db) =>
        {
            if (!IsAdmin(http)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var model = await db.ModelVersions.SingleOrDefaultAsync(m => m.Id == id);
            if (model is null) return Results.NotFound();
            if (body.OrganizationIds.Length == 0) return Results.BadRequest("Choose at least one organization.");
            var form = await db.Forms.Include(f => f.Publications).SingleAsync(f => f.Id == model.FormId);
            model.Status = ModelStatus.Published;
            model.PublishedAt ??= DateTimeOffset.UtcNow;
            form.PublishedModelId = model.Id;
            db.FormPublications.RemoveRange(form.Publications);
            foreach (var organizationId in body.OrganizationIds.Distinct())
                db.FormPublications.Add(new FormPublication { FormId = form.Id, OrganizationId = organizationId });
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }

    private static InterpretationRun NewRun(FormDefinition form) => new()
    {
        Id = Guid.NewGuid(),
        FormId = form.Id,
        Description = form.Description,
        Status = RunStatus.Queued,
        StatusText = "Waiting for the worker.",
        CreatedAt = DateTimeOffset.UtcNow
    };

    private static JsonElement? ParseOrNull(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static bool IsAdmin(HttpContext http) => ((AppUser)http.Items["user"]!).Role == "admin";
}

sealed record FormBody(string Address, string? Description);
sealed record DescriptionBody(string? Description);
sealed record ModelBody(JsonElement Model);
sealed record PublishBody(Guid[] OrganizationIds);
