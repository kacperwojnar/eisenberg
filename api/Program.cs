using System.IdentityModel.Tokens.Jwt;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Eisenberg.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using RabbitMQ.Client;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddEnvironmentVariables();

var databaseUrl = builder.Configuration["DATABASE_URL"]
    ?? throw new InvalidOperationException("DATABASE_URL is required.");
builder.Services.AddDbContext<EisenbergDb>(options =>
    options.UseNpgsql(ConnectionStrings.ToNpgsql(databaseUrl)));
builder.Services.AddSingleton<FileStore>();
builder.Services.AddSingleton<QueuePublisher>();
builder.Services.AddHttpClient<FoundryChatClient>();
var webOrigin = builder.Configuration["WEB_ORIGIN"]?.Trim().TrimEnd('/');
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.SetIsOriginAllowed(origin =>
                origin.StartsWith("http://localhost:", StringComparison.OrdinalIgnoreCase) ||
                origin.StartsWith("https://localhost:", StringComparison.OrdinalIgnoreCase) ||
                origin.StartsWith("http://127.0.0.1:", StringComparison.OrdinalIgnoreCase) ||
                origin.StartsWith("https://127.0.0.1:", StringComparison.OrdinalIgnoreCase) ||
                origin.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(webOrigin) && string.Equals(origin.TrimEnd('/'), webOrigin, StringComparison.OrdinalIgnoreCase)))
            .AllowAnyHeader()
            .AllowAnyMethod());
});

var app = builder.Build();
app.UseCors();
await Seed.EnsureAsync(app.Services);

app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/auth"))
    {
        await next();
        return;
    }
    var config = context.RequestServices.GetRequiredService<IConfiguration>();
    var developmentAuth = string.Equals(config["DEVELOPMENT_AUTH"], "true", StringComparison.OrdinalIgnoreCase);
    var tenant = config["ENTRA_TENANT_ID"];
    var clientId = config["ENTRA_API_CLIENT_ID"];
    var entraReady = !string.IsNullOrWhiteSpace(tenant) && !string.IsNullOrWhiteSpace(clientId);
    if (entraReady)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Sign in.");
            return;
        }
        var email = await EntraTokens.EmailAsync(header["Bearer ".Length..].Trim(), tenant!, clientId!, context.RequestAborted);
        if (email is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("The sign-in token was rejected.");
            return;
        }
        var db = context.RequestServices.GetRequiredService<EisenbergDb>();
        var user = await db.Users.Include(u => u.Memberships).SingleOrDefaultAsync(u => u.Email == email);
        if (user is null)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync("This account is not a user.");
            return;
        }
        context.Items["user"] = user;
        await next();
        return;
    }
    if (!developmentAuth)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsync("Entra External ID is not connected.");
        return;
    }

    var key = context.Request.Headers["X-Dev-User"].ToString();
    var devEmail = key switch
    {
        "admin" => Seed.AdminEmail,
        "user" => Seed.UserEmail,
        _ => null
    };
    if (devEmail is null)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsync("Select the seeded admin or user.");
        return;
    }

    var directory = context.RequestServices.GetRequiredService<EisenbergDb>();
    var devUser = await directory.Users.Include(u => u.Memberships).SingleAsync(u => u.Email == devEmail);
    context.Items["user"] = devUser;
    await next();
});

app.MapGet("/auth", (IConfiguration config) =>
{
    var tenant = config["ENTRA_TENANT_ID"];
    var clientId = config["ENTRA_API_CLIENT_ID"];
    if (string.IsNullOrWhiteSpace(tenant) || string.IsNullOrWhiteSpace(clientId))
        return Results.Json(new { mode = "development" });
    return Results.Json(new { mode = "entra", tenantId = tenant, clientId });
});

app.MapPost("/computer-use", async (ComputerUseBody body, HttpContext http, FoundryChatClient chat) =>
{
    if (Current(http).Role != "user") return Results.StatusCode(StatusCodes.Status403Forbidden);
    var point = await chat.PointAsync(body.Screenshot, body.PageLabel);
    return point is null ? Results.Json(new { connected = false }) : Results.Json(new { connected = true, x = point.Value.X, y = point.Value.Y });
});

app.MapGet("/me", (HttpContext http) =>
{
    var user = Current(http);
    return Results.Json(new
    {
        user.Id,
        user.Email,
        user.Role,
        organizationIds = user.Memberships.Select(m => m.OrganizationId)
    });
});

app.MapGet("/organizations", async (EisenbergDb db) =>
    Results.Json(await db.Organizations.OrderBy(o => o.Name).Select(o => new { o.Id, o.Name, o.SettingsText }).ToListAsync()));

app.MapPost("/organizations", async (NameBody body, HttpContext http, EisenbergDb db) =>
{
    if (!IsAdmin(http)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var organization = new Organization { Id = Guid.NewGuid(), Name = body.Name.Trim() };
    db.Organizations.Add(organization);
    await db.SaveChangesAsync();
    return Results.Json(new { organization.Id, organization.Name });
});

app.MapGet("/users", async (HttpContext http, EisenbergDb db) =>
{
    if (!IsAdmin(http)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var users = await db.Users.Include(u => u.Memberships).OrderBy(u => u.Email).ToListAsync();
    return Results.Json(users.Select(u => new
    {
        u.Id,
        u.Email,
        u.Role,
        u.ContentUnderstandingEnabled,
        organizationIds = u.Memberships.Select(m => m.OrganizationId)
    }));
});

app.MapPost("/users", async (CreateUserBody body, HttpContext http, EisenbergDb db) =>
{
    if (!IsAdmin(http)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (body.OrganizationIds.Length == 0) return Results.BadRequest("At least one organization is required.");
    if (body.Role is not ("admin" or "user")) return Results.BadRequest("Role must be admin or user.");
    var user = new AppUser
    {
        Id = Guid.NewGuid(),
        Email = body.Email.Trim(),
        Role = body.Role
    };
    foreach (var organizationId in body.OrganizationIds.Distinct())
        user.Memberships.Add(new Membership { UserId = user.Id, OrganizationId = organizationId });
    db.Users.Add(user);
    await db.SaveChangesAsync();
    return Results.Json(new { user.Id, user.Email, user.Role });
});

app.MapPost("/users/{id:guid}/organizations", async (Guid id, OrganizationIdBody body, HttpContext http, EisenbergDb db) =>
{
    if (!IsAdmin(http)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (await db.Memberships.AnyAsync(m => m.UserId == id && m.OrganizationId == body.OrganizationId))
        return Results.NoContent();
    db.Memberships.Add(new Membership { UserId = id, OrganizationId = body.OrganizationId });
    await db.SaveChangesAsync();
    return Results.NoContent();
});

app.MapGet("/admin/mismatches", async (HttpContext http, EisenbergDb db) =>
{
    if (!IsAdmin(http)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var rows = await db.SettingsMismatches.OrderByDescending(m => m.CreatedAt).ToListAsync();
    var orgs = await db.Organizations.ToDictionaryAsync(o => o.Id, o => o.Name);
    var users = await db.Users.ToDictionaryAsync(u => u.Id, u => u.Email);
    return Results.Json(rows.Select(m => new
    {
        m.Id,
        m.OrganizationId,
        organizationName = orgs.GetValueOrDefault(m.OrganizationId) ?? "",
        userEmail = users.GetValueOrDefault(m.UserId) ?? "",
        m.CreatedAt,
        m.Text,
        handled = m.HandledAt != null
    }));
});

app.MapGet("/admin/attention", async (HttpContext http, EisenbergDb db) =>
{
    if (!IsAdmin(http)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var forms = await db.Forms.ToDictionaryAsync(form => form.Id);
    var runs = await db.InterpretationRuns.OrderByDescending(run => run.CreatedAt).ToListAsync();
    var orgs = await db.Organizations.ToDictionaryAsync(org => org.Id, org => org.Name);
    var users = await db.Users.ToDictionaryAsync(user => user.Id, user => user.Email);
    var waiting = runs.Where(run => run.Status == RunStatus.WaitingForSignIn).Select(run => new
    {
        formId = run.FormId,
        address = forms.GetValueOrDefault(run.FormId)?.Address ?? "",
        runId = run.Id,
        liveViewUrl = run.LiveViewUrl,
        statusText = run.StatusText
    });
    var failed = runs.GroupBy(run => run.FormId).Select(group => group.First())
        .Where(run => run.Status is RunStatus.Failed or RunStatus.NotConnected)
        .Select(run => new
        {
            formId = run.FormId,
            address = forms.GetValueOrDefault(run.FormId)?.Address ?? "",
            runId = run.Id,
            run.Status,
            run.StatusText
        });
    var settings = (await db.SettingsMismatches.Where(item => item.HandledAt == null).OrderByDescending(item => item.CreatedAt).ToListAsync()).Select(item => new
    {
        item.Id,
        item.OrganizationId,
        organizationName = orgs.GetValueOrDefault(item.OrganizationId) ?? "",
        userEmail = users.GetValueOrDefault(item.UserId) ?? "",
        item.CreatedAt,
        item.Text
    });
    var changes = (await db.FormChangeReports.Where(item => item.HandledAt == null).OrderByDescending(item => item.CreatedAt).ToListAsync())
        .Select(item => new
        {
            item.Id,
            item.FormId,
            address = forms.GetValueOrDefault(item.FormId)?.Address ?? "",
            organizationName = orgs.GetValueOrDefault(item.OrganizationId) ?? "",
            userEmail = users.GetValueOrDefault(item.UserId) ?? "",
            item.CreatedAt,
            item.Text
        });
    return Results.Json(new { waiting, failed, settings, formChanges = changes });
});

app.MapPut("/organizations/{id:guid}/settings", async (Guid id, TextBody body, HttpContext http, EisenbergDb db) =>
{
    if (!IsAdmin(http)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var organization = await db.Organizations.SingleOrDefaultAsync(o => o.Id == id);
    if (organization is null) return Results.NotFound();
    organization.SettingsText = body.Text;
    var openReports = await db.SettingsMismatches.Where(item => item.OrganizationId == id && item.HandledAt == null).ToListAsync();
    var handledAt = DateTimeOffset.UtcNow;
    foreach (var report in openReports)
        report.HandledAt = handledAt;
    await db.SaveChangesAsync();
    return Results.NoContent();
});

app.MapGet("/content-understanding", async (HttpContext http, EisenbergDb db, IConfiguration configuration) =>
{
    if (!IsAdmin(http)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var global = await db.GlobalContentUnderstanding.SingleAsync();
    var connected = !string.IsNullOrWhiteSpace(configuration["CONTENT_UNDERSTANDING_ENDPOINT"])
        && !string.IsNullOrWhiteSpace(configuration["CONTENT_UNDERSTANDING_API_KEY"]);
    return Results.Json(new { global.Enabled, connected });
});

app.MapPut("/content-understanding", async (EnabledBody body, HttpContext http, EisenbergDb db) =>
{
    if (!IsAdmin(http)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var global = await db.GlobalContentUnderstanding.SingleAsync();
    global.Enabled = body.Enabled;
    await db.SaveChangesAsync();
    return Results.NoContent();
});

app.MapPut("/users/{id:guid}/content-understanding", async (Guid id, OptionalEnabledBody body, HttpContext http, EisenbergDb db) =>
{
    if (!IsAdmin(http)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var user = await db.Users.SingleOrDefaultAsync(u => u.Id == id);
    if (user is null) return Results.NotFound();
    user.ContentUnderstandingEnabled = body.Enabled;
    await db.SaveChangesAsync();
    return Results.NoContent();
});

app.MapGet("/admin/files", async (HttpContext http, EisenbergDb db) =>
{
    if (!IsAdmin(http)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var orgs = await db.Organizations.ToDictionaryAsync(o => o.Id, o => o.Name);
    var files = await db.Files.OrderByDescending(f => f.UploadedAt).ToListAsync();
    return Results.Json(files.Select(f => new
    {
        f.Id,
        f.OrganizationId,
        organizationName = orgs.GetValueOrDefault(f.OrganizationId) ?? "",
        f.OriginalFileName,
        f.Status,
        f.StatusText,
        f.ContentUnderstandingEnabled
    }));
});

app.MapPut("/files/{id:guid}/content-understanding", async (Guid id, OptionalEnabledBody body, HttpContext http, EisenbergDb db) =>
{
    if (!IsAdmin(http)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var file = await db.Files.SingleOrDefaultAsync(f => f.Id == id);
    if (file is null) return Results.NotFound();
    file.ContentUnderstandingEnabled = body.Enabled;
    await db.SaveChangesAsync();
    return Results.NoContent();
});

app.MapGet("/files", async (Guid? organizationId, HttpContext http, EisenbergDb db) =>
{
    var user = Current(http);
    if (user.Role != "user") return Results.StatusCode(StatusCodes.Status403Forbidden);
    var organizationIds = user.Memberships.Select(m => m.OrganizationId).ToArray();
    var query = db.Files.Where(f => organizationIds.Contains(f.OrganizationId) && f.UploadedByUserId == user.Id);
    if (organizationId is Guid selected)
    {
        if (!organizationIds.Contains(selected)) return Results.StatusCode(StatusCodes.Status403Forbidden);
        query = query.Where(f => f.OrganizationId == selected);
    }
    return Results.Json(await query.OrderByDescending(f => f.UploadedAt).Select(f => new
    {
        f.Id,
        f.OrganizationId,
        f.OriginalFileName,
        f.Status,
        f.StatusText,
        f.UploadedAt
    }).ToListAsync());
});

app.MapGet("/files/{id:guid}/reading", async (Guid id, HttpContext http, EisenbergDb db) =>
{
    var user = Current(http);
    if (user.Role != "user") return Results.StatusCode(StatusCodes.Status403Forbidden);
    var file = await db.Files.SingleOrDefaultAsync(item => item.Id == id);
    if (file is null) return Results.NotFound();
    if (file.UploadedByUserId != user.Id || !user.Memberships.Any(membership => membership.OrganizationId == file.OrganizationId))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    var instances = await db.Instances.Where(instance => instance.OrganizationId == file.OrganizationId).ToListAsync();
    return Results.Json(FileReading.Body(file, instances));
});

app.MapPost("/files", async (HttpRequest request, HttpContext http, EisenbergDb db, FileStore store, QueuePublisher publisher) =>
{
    var user = Current(http);
    if (user.Role != "user") return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (!publisher.IsConnected)
        return Results.Problem("RABBITMQ_URL is required.", statusCode: StatusCodes.Status503ServiceUnavailable);
    if (!request.HasFormContentType) return Results.BadRequest("Expected a file upload.");
    var form = await request.ReadFormAsync();
    if (!Guid.TryParse(form["organizationId"], out var organizationId))
        return Results.BadRequest("organizationId is required.");
    if (!user.Memberships.Any(m => m.OrganizationId == organizationId))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    var uploaded = form.Files.FirstOrDefault();
    if (uploaded is null) return Results.BadRequest("A file is required.");

    var file = new StoredFile
    {
        Id = Guid.NewGuid(),
        OrganizationId = organizationId,
        UploadedByUserId = user.Id,
        OriginalFileName = uploaded.FileName,
        UploadedAt = DateTimeOffset.UtcNow,
        Status = "stored"
    };
    file.StorageLocation = await store.SaveAsync(file.Id, uploaded);
    db.Files.Add(file);
    await db.SaveChangesAsync();
    publisher.PublishFileUploaded(file.Id);
    return Results.Json(new { file.Id, file.Status });
});

app.MapDelete("/files/{id:guid}", async (Guid id, HttpContext http, EisenbergDb db, FileStore store, QueuePublisher publisher) =>
{
    var user = Current(http);
    if (user.Role != "user") return Results.StatusCode(StatusCodes.Status403Forbidden);
    var file = await db.Files.SingleOrDefaultAsync(f => f.Id == id);
    if (file is null) return Results.NotFound();
    if (!user.Memberships.Any(m => m.OrganizationId == file.OrganizationId) || file.UploadedByUserId != user.Id)
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    var organizationId = file.OrganizationId;
    await store.DeleteAsync(file.StorageLocation);
    db.Files.Remove(file);
    await db.SaveChangesAsync();
    var another = await db.Files
        .Where(item => item.OrganizationId == organizationId && item.Reading != null && item.Reading != "")
        .Select(item => (Guid?)item.Id)
        .FirstOrDefaultAsync();
    if (another is Guid fileId && publisher.IsConnected)
        publisher.PublishFileUploaded(fileId);
    else
    {
        var generated = await db.Instances.Where(instance => instance.OrganizationId == organizationId && instance.Generated).ToListAsync();
        db.Instances.RemoveRange(generated);
        await db.SaveChangesAsync();
    }
    return Results.NoContent();
});

app.MapGet("/filled-forms", async (HttpContext http, EisenbergDb db) =>
{
    var user = Current(http);
    if (user.Role != "user") return Results.StatusCode(StatusCodes.Status403Forbidden);
    return Results.Json(await db.FilledForms.Where(f => f.UserId == user.Id)
        .OrderByDescending(f => f.CreatedAt)
        .Select(f => new { f.Id, f.OrganizationId, f.CreatedAt, f.Summary, f.FormAddress, f.RecordLabel })
        .ToListAsync());
});

app.MapPost("/filled-forms", async (SummaryBody body, HttpContext http, EisenbergDb db) =>
{
    var user = Current(http);
    if (user.Role != "user") return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (!user.Memberships.Any(m => m.OrganizationId == body.OrganizationId))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    var info = new FilledFormInfo
    {
        Id = Guid.NewGuid(),
        OrganizationId = body.OrganizationId,
        UserId = user.Id,
        CreatedAt = DateTimeOffset.UtcNow,
        Summary = body.Summary,
        FormAddress = body.FormAddress?.Trim() ?? "",
        RecordLabel = body.RecordLabel?.Trim() ?? "",
        InstanceId = body.InstanceId
    };
    db.FilledForms.Add(info);
    await db.SaveChangesAsync();
    return Results.Json(new { info.Id });
});

app.MapPost("/chat", async (MessageBody body, HttpContext http, FoundryChatClient chat) =>
{
    if (Current(http).Role != "user") return Results.StatusCode(StatusCodes.Status403Forbidden);
    var reply = await chat.ReplyAsync(body.Message);
    return Results.Json(new { reply });
});

app.MapPost("/settings-mismatches", async (MismatchBody body, HttpContext http, EisenbergDb db) =>
{
    var user = Current(http);
    if (user.Role != "user") return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (!user.Memberships.Any(m => m.OrganizationId == body.OrganizationId))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    db.SettingsMismatches.Add(new SettingsMismatch
    {
        Id = Guid.NewGuid(),
        OrganizationId = body.OrganizationId,
        UserId = user.Id,
        CreatedAt = DateTimeOffset.UtcNow,
        Text = body.Text
    });
    await db.SaveChangesAsync();
    return Results.NoContent();
});

app.MapPost("/form-changes", async (FormChangeBody body, HttpContext http, EisenbergDb db) =>
{
    var user = Current(http);
    if (user.Role != "user") return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (!user.Memberships.Any(membership => membership.OrganizationId == body.OrganizationId))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    var publishedHere = await db.FormPublications.AnyAsync(item => item.FormId == body.FormId && item.OrganizationId == body.OrganizationId);
    if (!publishedHere) return Results.BadRequest("That form is not published to this organization.");
    if (string.IsNullOrWhiteSpace(body.Text)) return Results.BadRequest("Text is required.");
    db.FormChangeReports.Add(new FormChangeReport
    {
        Id = Guid.NewGuid(),
        OrganizationId = body.OrganizationId,
        UserId = user.Id,
        FormId = body.FormId,
        CreatedAt = DateTimeOffset.UtcNow,
        Text = body.Text.Trim()
    });
    await db.SaveChangesAsync();
    return Results.NoContent();
});

app.MapForms();

app.MapGet("/published-models", async (HttpContext http, EisenbergDb db) =>
{
    var user = Current(http);
    if (user.Role != "user") return Results.StatusCode(StatusCodes.Status403Forbidden);
    var organizationIds = user.Memberships.Select(membership => membership.OrganizationId).ToArray();
    var rows = await db.FormPublications
        .Where(publication => organizationIds.Contains(publication.OrganizationId))
        .Join(db.Forms, publication => publication.FormId, form => form.Id, (publication, form) => new { publication.OrganizationId, form.Address, form.PublishedModelId })
        .Where(row => row.PublishedModelId != null)
        .ToListAsync();
    var names = await db.Organizations.Where(org => organizationIds.Contains(org.Id)).ToDictionaryAsync(org => org.Id, org => org.Name);
    var modelIds = rows.Select(row => row.PublishedModelId!.Value).Distinct().ToArray();
    var versions = await db.ModelVersions.Where(model => modelIds.Contains(model.Id)).ToDictionaryAsync(model => model.Id);
    var lists = await db.ChoiceLists.Where(list => modelIds.Contains(list.ModelId)).ToListAsync();
    return Results.Json(rows.Select(row =>
    {
        var id = row.PublishedModelId!.Value;
        return new
        {
            row.OrganizationId,
            organizationName = names.GetValueOrDefault(row.OrganizationId) ?? "",
            row.Address,
            modelId = row.PublishedModelId,
            types = versions.TryGetValue(id, out var version)
                ? InstanceShape.Types(version.ModelJson, lists.Where(list => list.ModelId == id))
                : Array.Empty<object>()
        };
    }));
});

app.MapGet("/form-instances", async (HttpContext http, EisenbergDb db) =>
{
    var user = Current(http);
    if (user.Role != "user") return Results.StatusCode(StatusCodes.Status403Forbidden);
    var organizationIds = user.Memberships.Select(membership => membership.OrganizationId).ToArray();
    var instances = await db.Instances.Where(instance => organizationIds.Contains(instance.OrganizationId)).OrderBy(instance => instance.Label).ToListAsync();
    var modelIds = instances.Select(instance => instance.ModelId).Distinct().ToArray();
    var models = await db.ModelVersions.Where(model => modelIds.Contains(model.Id)).ToDictionaryAsync(model => model.Id);
    var lists = await db.ChoiceLists.Where(list => modelIds.Contains(list.ModelId)).ToListAsync();
    var orgs = await db.Organizations.Where(org => organizationIds.Contains(org.Id)).ToDictionaryAsync(org => org.Id, org => org.Name);
    var forms = await db.Forms.Where(form => form.PublishedModelId != null && modelIds.Contains(form.PublishedModelId.Value)).ToListAsync();
    return Results.Json(new
    {
        instances = instances.Select(instance =>
        {
            var form = forms.FirstOrDefault(item => item.PublishedModelId == instance.ModelId);
            return new
            {
                instance.Id,
                instance.OrganizationId,
                organizationName = orgs.GetValueOrDefault(instance.OrganizationId) ?? "",
                instance.ModelId,
                formId = form?.Id,
                formAddress = form?.Address ?? "",
                instance.TypeName,
                instance.SubclassName,
                instance.Label,
                instance.Generated,
                values = InstanceShape.Parse(instance.ValuesJson),
                sources = InstanceShape.Parse(instance.SourcesJson),
                properties = models.TryGetValue(instance.ModelId, out var model) ? InstanceShape.Properties(model.ModelJson, lists.Where(list => list.ModelId == instance.ModelId)) : Array.Empty<object>()
            };
        })
    });
});

app.MapPut("/form-instances/{id:guid}", async (Guid id, InstanceBody body, HttpContext http, EisenbergDb db) =>
{
    var user = Current(http);
    if (user.Role != "user") return Results.StatusCode(StatusCodes.Status403Forbidden);
    var instance = await db.Instances.SingleOrDefaultAsync(item => item.Id == id);
    if (instance is null) return Results.NotFound();
    if (!user.Memberships.Any(membership => membership.OrganizationId == instance.OrganizationId))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (body.Values.ValueKind != JsonValueKind.Object) return Results.BadRequest("Values must be an object.");
    var edited = body.Values.EnumerateObject().ToDictionary(property => property.Name, property => (string?)property.Value.ToString());
    var editedProblems = await InstanceRules.Problems(db, instance.ModelId, instance.TypeName, instance.SubclassName, edited);
    if (editedProblems.Count > 0) return FieldErrors(editedProblems);
    instance.Label = body.Label?.Trim() ?? instance.Label;
    instance.SourcesJson = KeepSources(instance.SourcesJson, instance.ValuesJson, edited);
    instance.ValuesJson = body.Values.GetRawText();
    instance.Generated = false;
    instance.UpdatedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync();
    return Results.NoContent();
});

app.MapPost("/form-instances", async (NewInstanceBody body, HttpContext http, EisenbergDb db) =>
{
    var user = Current(http);
    if (user.Role != "user") return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (!user.Memberships.Any(membership => membership.OrganizationId == body.OrganizationId))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    var published = await db.Forms.AnyAsync(form => form.PublishedModelId == body.ModelId);
    if (!published) return Results.BadRequest("The model is not published.");
    if (body.Values.ValueKind != JsonValueKind.Object) return Results.BadRequest("Values must be an object.");
    var createdValues = body.Values.EnumerateObject().ToDictionary(property => property.Name, property => (string?)property.Value.ToString());
    var subclass = string.IsNullOrWhiteSpace(body.SubclassName) ? null : body.SubclassName.Trim();
    var createdProblems = await InstanceRules.Problems(db, body.ModelId, body.TypeName.Trim(), subclass, createdValues);
    if (createdProblems.Count > 0) return FieldErrors(createdProblems);
    var now = DateTimeOffset.UtcNow;
    var instance = new FormInstance
    {
        Id = Guid.NewGuid(),
        OrganizationId = body.OrganizationId,
        ModelId = body.ModelId,
        TypeName = body.TypeName.Trim(),
        SubclassName = subclass,
        Label = body.Label.Trim(),
        ValuesJson = body.Values.GetRawText(),
        Generated = false,
        CreatedAt = now,
        UpdatedAt = now
    };
    db.Instances.Add(instance);
    await db.SaveChangesAsync();
    return Results.Json(new { instance.Id });
});

app.MapGet("/form-instances/{id:guid}/fill-plan", async (Guid id, HttpContext http, EisenbergDb db) =>
{
    var user = Current(http);
    if (user.Role != "user") return Results.StatusCode(StatusCodes.Status403Forbidden);
    var instance = await db.Instances.SingleOrDefaultAsync(item => item.Id == id);
    if (instance is null) return Results.NotFound();
    if (!user.Memberships.Any(membership => membership.OrganizationId == instance.OrganizationId))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    var version = await db.ModelVersions.SingleOrDefaultAsync(model => model.Id == instance.ModelId);
    if (version is null) return Results.NotFound();
    var lists = await db.ChoiceLists.Where(list => list.ModelId == instance.ModelId).ToListAsync();
    var runtime = FormRuntime.Parse(version.ModelJson, lists.Select(list => (list.Id.ToString(), list.IsComplete, list.ChoicesJson)));
    using var valuesDocument = JsonDocument.Parse(string.IsNullOrWhiteSpace(instance.ValuesJson) ? "{}" : instance.ValuesJson);
    var values = valuesDocument.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => (string?)property.Value.ToString());
    var form = await db.Forms.Where(item => item.PublishedModelId == instance.ModelId)
        .Select(item => new { item.Id, item.Address })
        .FirstOrDefaultAsync();
    return Results.Json(new
    {
        formId = form?.Id,
        address = form?.Address,
        steps = runtime.Plan(instance.TypeName, instance.SubclassName, values)
    });
});

app.Run();

static AppUser Current(HttpContext http) => (AppUser)http.Items["user"]!;
static bool IsAdmin(HttpContext http) => Current(http).Role == "admin";

static IResult FieldErrors(IReadOnlyList<FieldProblem> problems) =>
    Results.Json(new { problems = problems.Select(problem => new { property = problem.Property, message = problem.Message }) }, statusCode: StatusCodes.Status400BadRequest);

static string KeepSources(string sourcesJson, string previousValuesJson, IReadOnlyDictionary<string, string?> edited)
{
    JsonObject sources;
    try
    {
        sources = JsonNode.Parse(string.IsNullOrWhiteSpace(sourcesJson) ? "{}" : sourcesJson) as JsonObject ?? new JsonObject();
    }
    catch (JsonException)
    {
        sources = new JsonObject();
    }
    var previous = new Dictionary<string, string?>();
    try
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(previousValuesJson) ? "{}" : previousValuesJson);
        if (document.RootElement.ValueKind == JsonValueKind.Object)
            previous = document.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => (string?)property.Value.ToString());
    }
    catch (JsonException)
    {
        previous = [];
    }
    foreach (var name in sources.Select(pair => pair.Key).ToList())
    {
        previous.TryGetValue(name, out var before);
        edited.TryGetValue(name, out var after);
        if (!string.Equals(before ?? "", after ?? "", StringComparison.Ordinal))
            sources.Remove(name);
    }
    return sources.ToJsonString();
}

sealed record NameBody(string Name);
sealed record CreateUserBody(string Email, string Role, Guid[] OrganizationIds);
sealed record OrganizationIdBody(Guid OrganizationId);
sealed record TextBody(string Text);
sealed record EnabledBody(bool Enabled);
sealed record OptionalEnabledBody(bool? Enabled);
sealed record SummaryBody(Guid OrganizationId, string Summary, string? FormAddress, string? RecordLabel, Guid? InstanceId);
sealed record MessageBody(string Message);
sealed record MismatchBody(Guid OrganizationId, string Text);
sealed record FormChangeBody(Guid OrganizationId, Guid FormId, string Text);
sealed record InstanceBody(string? Label, JsonElement Values);
sealed record NewInstanceBody(Guid OrganizationId, Guid ModelId, string TypeName, string? SubclassName, string Label, JsonElement Values);
sealed record ComputerUseBody(string Screenshot, string PageLabel);

static class FileReading
{
    public static object Body(StoredFile file, IEnumerable<FormInstance> instances)
    {
        var text = "";
        var segments = new List<object>();
        if (!string.IsNullOrWhiteSpace(file.Reading))
        {
            try
            {
                using var document = JsonDocument.Parse(file.Reading);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("text", out var textNode))
                    text = textNode.GetString() ?? "";
                else
                    text = file.Reading;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("segments", out var list) && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (var segment in list.EnumerateArray())
                    {
                        string location = "";
                        if (segment.TryGetProperty("location", out var place))
                            location = place.ValueKind == JsonValueKind.String ? place.GetString() ?? "" : place.GetRawText();
                        segments.Add(new
                        {
                            page = segment.TryGetProperty("page", out var page) && page.TryGetInt32(out var number) ? number : (int?)null,
                            text = segment.TryGetProperty("text", out var segmentText) ? segmentText.GetString() ?? "" : "",
                            location
                        });
                    }
                }
            }
            catch (JsonException)
            {
                text = file.Reading;
            }
        }

        var cited = new List<object>();
        foreach (var instance in instances)
        {
            var names = new List<string>();
            try
            {
                using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(instance.SourcesJson) ? "{}" : instance.SourcesJson);
                if (document.RootElement.ValueKind != JsonValueKind.Object) continue;
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (property.Value.ValueKind != JsonValueKind.Object) continue;
                    var fileId = property.Value.TryGetProperty("fileId", out var id) ? id.GetString() : null;
                    var fileName = property.Value.TryGetProperty("fileName", out var name) ? name.GetString() : null;
                    if (fileId == file.Id.ToString() || string.Equals(fileName, file.OriginalFileName, StringComparison.OrdinalIgnoreCase))
                        names.Add(property.Name);
                }
            }
            catch (JsonException)
            {
                continue;
            }
            if (names.Count > 0)
                cited.Add(new { instance.Id, instance.Label, instance.ModelId, properties = names });
        }

        return new { file.Status, file.StatusText, text, segments, records = cited };
    }
}

static class InstanceRules
{
    public static async Task<IReadOnlyList<FieldProblem>> Problems(EisenbergDb db, Guid modelId, string typeName, string? subclassName, IReadOnlyDictionary<string, string?> values)
    {
        var version = await db.ModelVersions.SingleOrDefaultAsync(model => model.Id == modelId);
        if (version is null) return [new FieldProblem(null, "The model was not found.")];
        var lists = await db.ChoiceLists.Where(list => list.ModelId == modelId).ToListAsync();
        var runtime = FormRuntime.Parse(version.ModelJson, lists.Select(list => (list.Id.ToString(), list.IsComplete, list.ChoicesJson)));
        return runtime.Problems(typeName, subclassName, values);
    }
}

static class InstanceShape
{
    public static JsonElement? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    public static object[] Properties(string modelJson, IEnumerable<ChoiceList> lists)
    {
        using var document = JsonDocument.Parse(modelJson);
        if (!document.RootElement.TryGetProperty("types", out var types)) return [];
        var byId = lists.ToDictionary(list => list.Id.ToString());
        var properties = new List<object>();
        foreach (var type in types.EnumerateArray())
        {
            if (!type.TryGetProperty("properties", out var items)) continue;
            foreach (var property in items.EnumerateArray())
            {
                var name = property.TryGetProperty("name", out var propertyName) ? propertyName.GetString() ?? "" : "";
                var description = property.TryGetProperty("description", out var propertyDescription) ? propertyDescription.GetString() ?? "" : "";
                var kind = property.TryGetProperty("kind", out var propertyKind) ? propertyKind.GetString() ?? "text" : "text";
                var required = property.TryGetProperty("required", out var propertyRequired) && propertyRequired.ValueKind == JsonValueKind.True;
                var pageLabel = "";
                if (property.TryGetProperty("controls", out var controls))
                {
                    foreach (var control in controls.EnumerateArray())
                    {
                        if (control.TryGetProperty("pageText", out var text) && text.GetString() is { Length: > 0 } value)
                        {
                            pageLabel = value;
                            break;
                        }
                    }
                }
            string[] options = [];
            var incomplete = false;
            if (property.TryGetProperty("choiceListId", out var choiceListId) && byId.TryGetValue(choiceListId.GetString() ?? "", out var list))
            {
                incomplete = !list.IsComplete;
                using var choices = JsonDocument.Parse(list.ChoicesJson);
                var array = choices.RootElement.ValueKind == JsonValueKind.Array
                    ? choices.RootElement
                    : choices.RootElement.TryGetProperty("choices", out var nested) ? nested : default;
                options = array.ValueKind == JsonValueKind.Array
                    ? array.EnumerateArray().Select(choice => choice.TryGetProperty("text", out var label) ? label.GetString() ?? "" : "").Where(text => text.Length > 0).ToArray()
                    : [];
            }
                properties.Add(new { name, description, kind, required, pageLabel, options, incomplete });
            }
        }
        return properties.ToArray();
    }

    public static object[] Types(string modelJson, IEnumerable<ChoiceList> lists)
    {
        var stored = lists.ToList();
        var runtime = FormRuntime.Parse(modelJson, stored.Select(list => (list.Id.ToString(), list.IsComplete, list.ChoicesJson)));
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(modelJson) ? """{"types":[]}""" : modelJson);
        if (!document.RootElement.TryGetProperty("types", out var types) || types.ValueKind != JsonValueKind.Array)
            return [];
        var result = new List<object>();
        foreach (var type in types.EnumerateArray())
        {
            var name = type.TryGetProperty("name", out var typeName) && typeName.ValueKind == JsonValueKind.String ? typeName.GetString() ?? "" : "";
            if (name.Length == 0) continue;
            var subclassOf = type.TryGetProperty("subclassOf", out var parent) && parent.ValueKind == JsonValueKind.String ? parent.GetString() : null;
            var properties = runtime.PropertiesOf(name, null).Select(property => new
            {
                name = property.Name,
                description = property.Description,
                kind = property.Kind,
                required = property.Required,
                pageLabel = property.Controls.Select(control => control.PageText).FirstOrDefault(text => text.Length > 0) ?? "",
                options = property.Choices.Select(choice => choice.Text).Where(text => text.Length > 0).ToArray(),
                incomplete = property.ChoiceListId is not null && !property.ChoiceListComplete
            }).ToArray();
            result.Add(new { name, subclassOf, properties });
        }
        return result.ToArray();
    }
}

sealed class FileStore
{
    private readonly string _root;
    private readonly string? _connection;

    public FileStore(IConfiguration configuration)
    {
        _connection = configuration["BLOB_CONNECTION_STRING"];
        _root = Path.GetFullPath(configuration["FILE_STORAGE_ROOT"] is { Length: > 0 } root
            ? root
            : Path.Combine(Directory.GetCurrentDirectory(), "..", "data", "files"));
    }

    public string Root => _root;

    public bool IsUnderRoot(string path)
    {
        var full = Path.GetFullPath(path);
        var prefix = _root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                     + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(full, _root, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<string> SaveAsync(Guid id, IFormFile file)
    {
        await using var stream = file.OpenReadStream();
        return await StoredFiles.SaveAsync(_connection, _root, id, stream);
    }

    public Task DeleteAsync(string path) => StoredFiles.DeleteAsync(_connection, path);
}

sealed class QueuePublisher : IDisposable
{
    private const string FileUploaded = "file-uploaded";
    private const string InterpretationRequested = "form-interpretation-requested";
    private readonly IConnection? _connection;
    private readonly IModel? _channel;
    private readonly object _gate = new();

    public QueuePublisher(IConfiguration configuration)
    {
        var url = configuration["RABBITMQ_URL"];
        if (string.IsNullOrWhiteSpace(url))
            return;
        var factory = new ConnectionFactory { Uri = new Uri(url) };
        _connection = factory.CreateConnection();
        _channel = _connection.CreateModel();
        _channel.QueueDeclare(FileUploaded, durable: true, exclusive: false, autoDelete: false);
        _channel.QueueDeclare(InterpretationRequested, durable: true, exclusive: false, autoDelete: false);
    }

    public bool IsConnected => _channel is not null;

    public void PublishFileUploaded(Guid fileId) => Publish(FileUploaded, fileId);

    public void PublishInterpretationRequested(Guid runId) => Publish(InterpretationRequested, runId);

    private void Publish(string queue, Guid id)
    {
        if (_channel is null)
            throw new InvalidOperationException("RABBITMQ_URL is required.");
        // A RabbitMQ channel must not be used from two requests at once.
        lock (_gate)
            _channel.BasicPublish("", queue, body: Encoding.UTF8.GetBytes(id.ToString()));
    }

    public void Dispose()
    {
        _channel?.Dispose();
        _connection?.Dispose();
    }
}

sealed class FoundryChatClient(IConfiguration configuration, HttpClient http)
{
    public async Task<string> ReplyAsync(string message)
    {
        var endpoint = configuration["FOUNDRY_ENDPOINT"];
        var key = configuration["FOUNDRY_API_KEY"];
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(key))
            return "The model is not connected.";

        var deployment = configuration["FOUNDRY_CHAT_DEPLOYMENT"];
        if (string.IsNullOrWhiteSpace(deployment))
            deployment = "claude-haiku-5-5";

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint.TrimEnd('/') + "/anthropic/v1/messages");
        request.Headers.TryAddWithoutValidation("api-key", key);
        request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        request.Content = JsonContent.Create(new
        {
            model = deployment,
            max_tokens = 1024,
            messages = new[] { new { role = "user", content = message } }
        });
        using var response = await http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            return "The model call failed.";

        using var document = System.Text.Json.JsonDocument.Parse(body);
        if (document.RootElement.TryGetProperty("content", out var content))
        {
            foreach (var block in content.EnumerateArray())
            {
                if (block.TryGetProperty("text", out var text))
                    return text.GetString() ?? "";
            }
        }
        return body;
    }

    public async Task<(int X, int Y)?> PointAsync(string screenshot, string pageLabel)
    {
        var endpoint = configuration["FOUNDRY_ENDPOINT"];
        var key = configuration["FOUNDRY_API_KEY"];
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(screenshot))
            return null;
        var deployment = configuration["FOUNDRY_CHAT_DEPLOYMENT"];
        if (string.IsNullOrWhiteSpace(deployment))
            deployment = "claude-haiku-5-5";
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint.TrimEnd('/') + "/anthropic/v1/messages");
        request.Headers.TryAddWithoutValidation("api-key", key);
        request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        request.Content = JsonContent.Create(new
        {
            model = deployment,
            max_tokens = 200,
            messages = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "image", source = new { type = "base64", media_type = "image/jpeg", data = screenshot } },
                        new { type = "text", text = $"Point at the control labeled \"{pageLabel}\". Return only JSON {{\"x\":0,\"y\":0}} in CSS pixels, origin top-left." }
                    }
                }
            }
        });
        using var response = await http.SendAsync(request);
        var reply = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) return null;
        using var document = JsonDocument.Parse(reply);
        var text = document.RootElement.GetProperty("content").EnumerateArray().Select(block => block.TryGetProperty("text", out var value) ? value.GetString() : null).FirstOrDefault(value => value is not null) ?? "";
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        using var point = JsonDocument.Parse(text[start..(end + 1)]);
        if (!point.RootElement.TryGetProperty("x", out var x) || !point.RootElement.TryGetProperty("y", out var y)) return null;
        return ((int)Math.Round(x.GetDouble()), (int)Math.Round(y.GetDouble()));
    }
}

static class Seed
{
    public const string AdminEmail = "admin@eisenberg.local";
    public const string UserEmail = "user@eisenberg.local";

    public static async Task EnsureAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EisenbergDb>();
        await Schema.EnsureAsync(db);
        if (!await db.GlobalContentUnderstanding.AnyAsync())
            db.GlobalContentUnderstanding.Add(new GlobalContentUnderstanding { Id = 1, Enabled = false });
        if (await db.Users.AnyAsync())
        {
            await db.SaveChangesAsync();
            return;
        }

        var north = new Organization { Id = Guid.NewGuid(), Name = "North" };
        var south = new Organization { Id = Guid.NewGuid(), Name = "South" };
        var admin = new AppUser { Id = Guid.NewGuid(), Email = AdminEmail, Role = "admin" };
        var user = new AppUser { Id = Guid.NewGuid(), Email = UserEmail, Role = "user" };
        admin.Memberships.Add(new Membership { UserId = admin.Id, OrganizationId = north.Id });
        user.Memberships.Add(new Membership { UserId = user.Id, OrganizationId = north.Id });
        user.Memberships.Add(new Membership { UserId = user.Id, OrganizationId = south.Id });
        db.AddRange(north, south, admin, user);
        await db.SaveChangesAsync();
    }
}

static class EntraTokens
{
    private static readonly HttpClient Http = new();
    private static ConfigurationManager<OpenIdConnectConfiguration>? _configuration;
    private static string? _authority;

    public static async Task<string?> EmailAsync(string token, string tenant, string clientId, CancellationToken cancellationToken)
    {
        var authority = $"https://{tenant}.ciamlogin.com/{tenant}/v2.0";
        if (_configuration is null || _authority != authority)
        {
            _authority = authority;
            _configuration = new ConfigurationManager<OpenIdConnectConfiguration>(
                authority + "/.well-known/openid-configuration",
                new OpenIdConnectConfigurationRetriever(),
                new HttpDocumentRetriever(Http));
        }
        OpenIdConnectConfiguration configuration;
        try
        {
            configuration = await _configuration.GetConfigurationAsync(cancellationToken);
        }
        catch (Exception)
        {
            return null;
        }
        var handler = new JwtSecurityTokenHandler();
        try
        {
            var principal = handler.ValidateToken(token, new TokenValidationParameters
            {
                ValidIssuer = configuration.Issuer,
                ValidAudiences = [clientId],
                IssuerSigningKeys = configuration.SigningKeys
            }, out _);
            return principal.FindFirst("email")?.Value ?? principal.FindFirst("preferred_username")?.Value;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
