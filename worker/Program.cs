using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Eisenberg.Data;
using Eisenberg.Worker.Instances;
using Eisenberg.Worker.Interpretation;
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
builder.Services.AddHttpClient("interpretation", client => client.Timeout = TimeSpan.FromMinutes(6));
builder.Services.AddSingleton<Interpreter>();
builder.Services.AddSingleton<InstanceBuilder>();
builder.Services.AddHostedService<FileUploadedConsumer>();
builder.Services.AddHostedService<InterpretationConsumer>();

var app = builder.Build();
app.Run();

sealed class FileUploadedConsumer(IServiceProvider services, IConfiguration configuration, IHttpClientFactory httpFactory, InstanceBuilder instances, ILogger<FileUploadedConsumer> logger) : BackgroundService
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
        file.StatusText = "Reading the document.";
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            var global = await db.GlobalContentUnderstanding.SingleAsync(cancellationToken);
            var uploader = await db.Users.SingleAsync(user => user.Id == file.UploadedByUserId, cancellationToken);
            var localRead = false;
            if (string.IsNullOrWhiteSpace(file.Reading))
            {
                var enabled = ContentUnderstandingRules.IsEnabled(global.Enabled, uploader.ContentUnderstandingEnabled, file.ContentUnderstandingEnabled);
                if (!enabled)
                {
                    file.Status = "stored";
                    file.StatusText = "Reading is off for this file.";
                    await db.SaveChangesAsync(cancellationToken);
                    return;
                }
                if (ContentUnderstandingConfigured())
                    file.Reading = await ReadAsync(file.StorageLocation, cancellationToken);
                else
                {
                    file.Reading = await TryLocalTextAsync(file, cancellationToken);
                    localRead = file.Reading is not null;
                    if (!localRead)
                    {
                        file.Status = "stored";
                        file.StatusText = "Content Understanding is not connected, and this file is not plain text.";
                        await db.SaveChangesAsync(cancellationToken);
                        return;
                    }
                }
            }

            if (EmbeddingsConfigured() && file.Reading is not null)
            {
                await WriteQdrantAsync(file, cancellationToken);
                file.Status = "ready";
                file.StatusText = localRead ? "Read locally and indexed." : "Read and indexed.";
            }
            else
            {
                file.Status = "stored";
                file.StatusText = localRead
                    ? "Read locally. Content Understanding is not connected. Indexing did not run because Foundry or Qdrant is not connected."
                    : "Read. Indexing did not run because Foundry or Qdrant is not connected.";
            }
            await db.SaveChangesAsync(cancellationToken);
            await instances.BuildAsync(file.OrganizationId, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Processing {FileId} failed", fileId);
            file.Status = "failed";
            file.StatusText = ex.Message.Length > 300 ? ex.Message[..300] : ex.Message;
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task<string?> TryLocalTextAsync(StoredFile file, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(file.OriginalFileName).ToLowerInvariant();
        if (extension is ".pdf" or ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".zip" or ".docx" or ".xlsx" or ".doc")
            return null;
        await using var stream = await StoredFiles.OpenReadAsync(configuration["BLOB_CONNECTION_STRING"], file.StorageLocation, cancellationToken);
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, cancellationToken);
        var bytes = memory.ToArray();
        if (bytes.Length == 0 || bytes.Length > 1_000_000 || bytes.Contains((byte)0))
            return null;
        try
        {
            var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes).Trim();
            if (text.Length == 0) return null;
            return JsonSerializer.Serialize(new
            {
                text,
                segments = new[] { new { page = 1, text, location = "local text" } }
            });
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private bool ContentUnderstandingConfigured() =>
        !string.IsNullOrWhiteSpace(configuration["CONTENT_UNDERSTANDING_ENDPOINT"]) &&
        !string.IsNullOrWhiteSpace(configuration["CONTENT_UNDERSTANDING_API_KEY"]);

    private bool EmbeddingsConfigured() =>
        !string.IsNullOrWhiteSpace(configuration["FOUNDRY_ENDPOINT"]) &&
        !string.IsNullOrWhiteSpace(configuration["FOUNDRY_API_KEY"]) &&
        !string.IsNullOrWhiteSpace(configuration["QDRANT_URL"]);

    private string EmbeddingDeployment() =>
        string.IsNullOrWhiteSpace(configuration["FOUNDRY_EMBEDDING_DEPLOYMENT"])
            ? "text-embedding-3-large"
            : configuration["FOUNDRY_EMBEDDING_DEPLOYMENT"]!;

    private async Task<string> ReadAsync(string path, CancellationToken cancellationToken)
    {
        var http = httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromMinutes(5);
        var endpoint = configuration["CONTENT_UNDERSTANDING_ENDPOINT"]!.TrimEnd('/');
        var analyze = endpoint.Contains("/analyzers/", StringComparison.OrdinalIgnoreCase)
            ? endpoint
            : endpoint + "/contentunderstanding/analyzers/prebuilt-document:analyze?api-version=2025-05-01-preview";
        await using var stream = await StoredFiles.OpenReadAsync(configuration["BLOB_CONNECTION_STRING"], path, cancellationToken);
        using var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var request = new HttpRequestMessage(HttpMethod.Post, analyze);
        request.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Key", configuration["CONTENT_UNDERSTANDING_API_KEY"]);
        request.Content = content;
        using var response = await http.SendAsync(request, cancellationToken);
        if ((int)response.StatusCode == 202 && response.Headers.TryGetValues("Operation-Location", out var locations))
            return ToReading(await PollAsync(http, locations.First(), cancellationToken));
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("Content Understanding returned " + (int)response.StatusCode);
        return ToReading(body);
    }

    private async Task<string> PollAsync(HttpClient http, string operation, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 60; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Get, operation);
            request.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Key", configuration["CONTENT_UNDERSTANDING_API_KEY"]);
            using var response = await http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException("Content Understanding returned " + (int)response.StatusCode);
            using var document = JsonDocument.Parse(body);
            var status = document.RootElement.TryGetProperty("status", out var value) ? value.GetString() : null;
            if (string.Equals(status, "Failed", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Content Understanding failed.");
            if (string.Equals(status, "Succeeded", StringComparison.OrdinalIgnoreCase))
                return body;
        }
        throw new InvalidOperationException("Content Understanding did not finish.");
    }

    private static string ToReading(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement.TryGetProperty("result", out var result) ? result : document.RootElement;
            if (!root.TryGetProperty("contents", out var contents)) return body;
            var segments = new List<object>();
            var text = new StringBuilder();
            foreach (var content in contents.EnumerateArray())
            {
                if (content.TryGetProperty("markdown", out var markdown))
                    text.AppendLine(markdown.GetString());
                if (!content.TryGetProperty("pages", out var pages)) continue;
                foreach (var page in pages.EnumerateArray())
                {
                    var pageNumber = page.TryGetProperty("pageNumber", out var number) ? number.GetInt32() : segments.Count + 1;
                    var words = new StringBuilder();
                    string? location = null;
                    if (page.TryGetProperty("words", out var wordList))
                    {
                        foreach (var word in wordList.EnumerateArray())
                        {
                            if (word.TryGetProperty("content", out var wordText))
                                words.Append(wordText.GetString()).Append(' ');
                            if (location is null && word.TryGetProperty("source", out var source))
                                location = source.GetRawText();
                        }
                    }
                    var pageText = words.Length > 0 ? words.ToString().Trim() : page.TryGetProperty("content", out var pageContent) ? pageContent.GetString() ?? "" : "";
                    if (pageText.Length == 0) continue;
                    segments.Add(new { page = pageNumber, text = pageText, location });
                }
            }
            if (segments.Count == 0 && text.Length == 0) return body;
            return JsonSerializer.Serialize(new { text = text.ToString().Trim(), segments });
        }
        catch (JsonException)
        {
            return body;
        }
    }

    private async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken)
    {
        var endpoint = configuration["FOUNDRY_ENDPOINT"]!.TrimEnd('/');
        var deployment = EmbeddingDeployment();
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

    private async Task WriteQdrantAsync(StoredFile file, CancellationToken cancellationToken)
    {
        var segments = ReadingSegments(file.Reading!);
        if (segments.Count == 0) return;
        var first = await EmbedAsync(segments[0].Text, cancellationToken);
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
                vectors = new { size = first.Length, distance = "Cosine" }
            }, cancellationToken);
            created.EnsureSuccessStatusCode();
        }

        var points = new List<object>();
        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            var segmentVector = index == 0 ? first : await EmbedAsync(segment.Text, cancellationToken);
            var bytes = file.Id.ToByteArray();
            bytes[14] = (byte)(segment.Page / 256);
            bytes[15] = (byte)segment.Page;
            points.Add(new
            {
                id = new Guid(bytes),
                vector = segmentVector,
                payload = new { fileId = file.Id, organizationId = file.OrganizationId, text = segment.Text, page = segment.Page, location = segment.Location }
            });
        }
        using var upsert = await http.PutAsJsonAsync(collection + "/points", new { points }, cancellationToken);
        upsert.EnsureSuccessStatusCode();
    }

    private static List<(int Page, string Text, string? Location)> ReadingSegments(string reading)
    {
        try
        {
            using var document = JsonDocument.Parse(reading);
            if (!document.RootElement.TryGetProperty("segments", out var segments) || segments.GetArrayLength() == 0)
                return [(0, document.RootElement.TryGetProperty("text", out var text) ? text.GetString() ?? reading : reading, null)];
            return segments.EnumerateArray().Select(segment => (
                segment.TryGetProperty("page", out var page) ? page.GetInt32() : 0,
                segment.TryGetProperty("text", out var text) ? text.GetString() ?? "" : "",
                segment.TryGetProperty("location", out var location) ? location.GetRawText() : null
            )).Where(segment => segment.Item2.Length > 0).ToList();
        }
        catch (JsonException)
        {
            return [(0, reading, null)];
        }
    }
}
