using System.Text;
using Eisenberg.Data;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;

EnvFile.Load(Path.Combine(Directory.GetCurrentDirectory(), "..", ".env"));

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddEnvironmentVariables();

var databaseUrl = builder.Configuration["DATABASE_URL"]
    ?? throw new InvalidOperationException("DATABASE_URL is required.");
builder.Services.AddDbContext<EisenbergDb>(options =>
    options.UseNpgsql(ConnectionStrings.ToNpgsql(databaseUrl)));
builder.Services.AddSingleton<FileStore>();
builder.Services.AddSingleton<FileUploadedPublisher>();
builder.Services.AddHttpClient<FoundryChatClient>();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.SetIsOriginAllowed(origin =>
                origin.StartsWith("http://localhost:", StringComparison.OrdinalIgnoreCase) ||
                origin.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase))
            .AllowAnyHeader()
            .AllowAnyMethod());
});

var app = builder.Build();
app.UseCors();
await Seed.EnsureAsync(app.Services);

app.Use(async (context, next) =>
{
    var config = context.RequestServices.GetRequiredService<IConfiguration>();
    var developmentAuth = string.Equals(config["DEVELOPMENT_AUTH"], "true", StringComparison.OrdinalIgnoreCase);
    var entraReady = !string.IsNullOrWhiteSpace(config["ENTRA_TENANT_ID"]) &&
                     !string.IsNullOrWhiteSpace(config["ENTRA_API_CLIENT_ID"]);
    if (!developmentAuth || entraReady)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsync("Entra External ID is not connected.");
        return;
    }

    var key = context.Request.Headers["X-Dev-User"].ToString();
    var email = key switch
    {
        "admin" => Seed.AdminEmail,
        "user" => Seed.UserEmail,
        _ => null
    };
    if (email is null)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsync("Select the seeded admin or user.");
        return;
    }

    var db = context.RequestServices.GetRequiredService<EisenbergDb>();
    var user = await db.Users.Include(u => u.Memberships).SingleAsync(u => u.Email == email);
    context.Items["user"] = user;
    await next();
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
    return Results.Json(await db.SettingsMismatches.OrderByDescending(m => m.CreatedAt)
        .Select(m => new { m.Id, m.OrganizationId, m.UserId, m.CreatedAt, m.Text }).ToListAsync());
});

app.MapPut("/organizations/{id:guid}/settings", async (Guid id, TextBody body, HttpContext http, EisenbergDb db) =>
{
    if (!IsAdmin(http)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var organization = await db.Organizations.SingleOrDefaultAsync(o => o.Id == id);
    if (organization is null) return Results.NotFound();
    organization.SettingsText = body.Text;
    await db.SaveChangesAsync();
    return Results.NoContent();
});

app.MapGet("/content-understanding", async (HttpContext http, EisenbergDb db) =>
{
    if (!IsAdmin(http)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var global = await db.GlobalContentUnderstanding.SingleAsync();
    return Results.Json(new { global.Enabled });
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
    return Results.Json(await db.Files.OrderByDescending(f => f.UploadedAt).Select(f => new
    {
        f.Id,
        f.OrganizationId,
        f.OriginalFileName,
        f.Status,
        f.ContentUnderstandingEnabled
    }).ToListAsync());
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
    var query = db.Files.Where(f => organizationIds.Contains(f.OrganizationId));
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
        f.UploadedAt
    }).ToListAsync());
});

app.MapPost("/files", async (HttpRequest request, HttpContext http, EisenbergDb db, FileStore store, FileUploadedPublisher publisher) =>
{
    var user = Current(http);
    if (user.Role != "user") return Results.StatusCode(StatusCodes.Status403Forbidden);
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
    publisher.Publish(file.Id);
    return Results.Json(new { file.Id, file.Status });
});

app.MapDelete("/files/{id:guid}", async (Guid id, HttpContext http, EisenbergDb db, FileStore store) =>
{
    var user = Current(http);
    if (user.Role != "user") return Results.StatusCode(StatusCodes.Status403Forbidden);
    var file = await db.Files.SingleOrDefaultAsync(f => f.Id == id);
    if (file is null) return Results.NotFound();
    if (!user.Memberships.Any(m => m.OrganizationId == file.OrganizationId))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    store.Delete(file.StorageLocation);
    db.Files.Remove(file);
    await db.SaveChangesAsync();
    return Results.NoContent();
});

app.MapGet("/filled-forms", async (HttpContext http, EisenbergDb db) =>
{
    var user = Current(http);
    if (user.Role != "user") return Results.StatusCode(StatusCodes.Status403Forbidden);
    return Results.Json(await db.FilledForms.Where(f => f.UserId == user.Id)
        .OrderByDescending(f => f.CreatedAt)
        .Select(f => new { f.Id, f.OrganizationId, f.CreatedAt, f.Summary })
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
        Summary = body.Summary
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

app.Run();

static AppUser Current(HttpContext http) => (AppUser)http.Items["user"]!;
static bool IsAdmin(HttpContext http) => Current(http).Role == "admin";

sealed record NameBody(string Name);
sealed record CreateUserBody(string Email, string Role, Guid[] OrganizationIds);
sealed record OrganizationIdBody(Guid OrganizationId);
sealed record TextBody(string Text);
sealed record EnabledBody(bool Enabled);
sealed record OptionalEnabledBody(bool? Enabled);
sealed record SummaryBody(Guid OrganizationId, string Summary);
sealed record MessageBody(string Message);
sealed record MismatchBody(Guid OrganizationId, string Text);

sealed class FileStore
{
    private readonly string _root = Environment.GetEnvironmentVariable("FILE_STORAGE_ROOT")
        ?? Path.Combine(Directory.GetCurrentDirectory(), "..", "data", "files");

    public async Task<string> SaveAsync(Guid id, IFormFile file)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, id.ToString());
        await using var stream = File.Create(path);
        await file.CopyToAsync(stream);
        return path;
    }

    public void Delete(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }
}

sealed class FileUploadedPublisher : IDisposable
{
    private readonly IConnection? _connection;
    private readonly IModel? _channel;

    public FileUploadedPublisher(IConfiguration configuration)
    {
        var url = configuration["RABBITMQ_URL"];
        if (string.IsNullOrWhiteSpace(url))
            return;
        var factory = new ConnectionFactory { Uri = new Uri(url) };
        _connection = factory.CreateConnection();
        _channel = _connection.CreateModel();
        _channel.QueueDeclare("file-uploaded", durable: true, exclusive: false, autoDelete: false);
    }

    public void Publish(Guid fileId)
    {
        if (_channel is null)
            throw new InvalidOperationException("RABBITMQ_URL is required.");
        _channel.BasicPublish("", "file-uploaded", body: Encoding.UTF8.GetBytes(fileId.ToString()));
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
}

static class Seed
{
    public const string AdminEmail = "admin@eisenberg.local";
    public const string UserEmail = "user@eisenberg.local";

    public static async Task EnsureAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EisenbergDb>();
        await db.Database.EnsureCreatedAsync();
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
