using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Eisenberg.Data;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddEnvironmentVariables();
var databaseUrl = builder.Configuration["DATABASE_URL"]
    ?? throw new InvalidOperationException("DATABASE_URL is required.");
builder.Services.AddDbContext<EisenbergDb>(options =>
    options.UseNpgsql(ConnectionStrings.ToNpgsql(databaseUrl)));
builder.Services.AddHttpClient();
builder.Services.AddHostedService<FileUploadedConsumer>();

var app = builder.Build();
app.Run();

sealed class FileUploadedConsumer(IServiceProvider services, IConfiguration configuration, IHttpClientFactory httpFactory, ILogger<FileUploadedConsumer> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var url = configuration["RABBITMQ_URL"]
            ?? throw new InvalidOperationException("RABBITMQ_URL is required.");
        var factory = new ConnectionFactory { Uri = new Uri(url), DispatchConsumersAsync = true };
        var connection = factory.CreateConnection();
        var channel = connection.CreateModel();
        channel.QueueDeclare("file-uploaded", durable: true, exclusive: false, autoDelete: false);
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.Received += async (_, delivery) =>
        {
            try
            {
                var id = Guid.Parse(Encoding.UTF8.GetString(delivery.Body.Span));
                await HandleAsync(id, stoppingToken);
                channel.BasicAck(delivery.DeliveryTag, false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "file-uploaded failed");
                channel.BasicNack(delivery.DeliveryTag, false, false);
            }
        };
        channel.BasicConsume("file-uploaded", autoAck: false, consumer);
        stoppingToken.Register(() =>
        {
            channel.Dispose();
            connection.Dispose();
        });
        return Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private async Task HandleAsync(Guid fileId, CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EisenbergDb>();
        var file = await db.Files.SingleOrDefaultAsync(f => f.Id == fileId, cancellationToken);
        if (file is null)
            return;

        file.Status = "processing";
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            var uploader = await db.Users.SingleAsync(u => u.Id == file.UploadedByUserId, cancellationToken);
            var global = await db.GlobalContentUnderstanding.SingleAsync(cancellationToken);
            string? text = null;
            if (ContentUnderstandingEnabled(global.Enabled) && ContentUnderstandingConfigured())
            {
                text = await ReadAsync(file.StorageLocation, cancellationToken);
            }
            else if (!ContentUnderstandingEnabled(global.Enabled) || !ContentUnderstandingConfigured())
            {
                file.Status = "stored";
                await db.SaveChangesAsync(cancellationToken);
                return;
            }

            if (EmbeddingsConfigured() && text is not null)
            {
                var vector = await EmbedAsync(text, cancellationToken);
                await WriteQdrantAsync(file, text, vector, cancellationToken);
                file.Status = "ready";
            }
            else
            {
                file.Status = "stored";
            }
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Processing {FileId} failed", fileId);
            file.Status = "failed";
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    // Only the global switch is applied until the combination of global, user, and import values is decided.
    private static bool ContentUnderstandingEnabled(bool globalEnabled) => globalEnabled;

    private bool ContentUnderstandingConfigured() =>
        !string.IsNullOrWhiteSpace(configuration["CONTENT_UNDERSTANDING_ENDPOINT"]) &&
        !string.IsNullOrWhiteSpace(configuration["CONTENT_UNDERSTANDING_API_KEY"]);

    private bool EmbeddingsConfigured() =>
        !string.IsNullOrWhiteSpace(configuration["FOUNDRY_ENDPOINT"]) &&
        !string.IsNullOrWhiteSpace(configuration["FOUNDRY_API_KEY"]) &&
        !string.IsNullOrWhiteSpace(configuration["FOUNDRY_EMBEDDING_DEPLOYMENT"]) &&
        !string.IsNullOrWhiteSpace(configuration["QDRANT_URL"]);

    private async Task<string> ReadAsync(string path, CancellationToken cancellationToken)
    {
        var http = httpFactory.CreateClient();
        await using var stream = File.OpenRead(path);
        using var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var request = new HttpRequestMessage(HttpMethod.Post, configuration["CONTENT_UNDERSTANDING_ENDPOINT"]);
        request.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Key", configuration["CONTENT_UNDERSTANDING_API_KEY"]);
        request.Content = content;
        using var response = await http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("Content Understanding returned " + (int)response.StatusCode);
        return body;
    }

    private async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken)
    {
        var endpoint = configuration["FOUNDRY_ENDPOINT"]!.TrimEnd('/');
        var deployment = configuration["FOUNDRY_EMBEDDING_DEPLOYMENT"];
        var url = $"{endpoint}/openai/deployments/{deployment}/embeddings?api-version=2024-10-21";
        var http = httpFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.TryAddWithoutValidation("api-key", configuration["FOUNDRY_API_KEY"]);
        request.Content = JsonContent.Create(new { input = text });
        using var response = await http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("Embeddings returned " + (int)response.StatusCode);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("data")[0].GetProperty("embedding").EnumerateArray().Select(n => n.GetSingle()).ToArray();
    }

    private async Task WriteQdrantAsync(StoredFile file, string text, float[] vector, CancellationToken cancellationToken)
    {
        var root = configuration["QDRANT_URL"]!.TrimEnd('/');
        var http = httpFactory.CreateClient();
        if (!string.IsNullOrWhiteSpace(configuration["QDRANT_API_KEY"]))
            http.DefaultRequestHeaders.TryAddWithoutValidation("api-key", configuration["QDRANT_API_KEY"]);

        var collection = root + "/collections/documents";
        using var existing = await http.GetAsync(collection, cancellationToken);
        if (existing.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            using var created = await http.PutAsJsonAsync(collection, new
            {
                vectors = new { size = vector.Length, distance = "Cosine" }
            }, cancellationToken);
            created.EnsureSuccessStatusCode();
        }

        using var upsert = await http.PutAsJsonAsync(collection + "/points", new
        {
            points = new[]
            {
                new
                {
                    id = file.Id,
                    vector,
                    payload = new { fileId = file.Id, organizationId = file.OrganizationId, text }
                }
            }
        }, cancellationToken);
        upsert.EnsureSuccessStatusCode();
    }
}
