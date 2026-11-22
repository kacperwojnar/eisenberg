using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Eisenberg.Worker.Interpretation;

sealed class McpException(string message) : Exception(message);

sealed record McpToolResult(bool IsError, string Text, JsonNode? Structured, IReadOnlyList<McpImage> Images)
{
    public static McpToolResult From(JsonNode? result)
    {
        var text = new StringBuilder();
        var images = new List<McpImage>();
        foreach (var item in Json.Array(result?["content"]))
        {
            var type = Json.Str(item?["type"]);
            if (type == "text") text.AppendLine(Json.Str(item?["text"]));
            else if (type == "image") images.Add(new McpImage(Json.Str(item?["mimeType"]), Json.Str(item?["data"])));
        }
        return new McpToolResult(Json.Bool(result?["isError"]), text.ToString().Trim(), result?["structuredContent"], images);
    }
}

sealed record McpImage(string MimeType, string Base64);

// Streamable HTTP transport of the Model Context Protocol: JSON-RPC over POST, answered with JSON or an event stream.
sealed class McpClient(HttpClient http, Uri endpoint, string apiKey)
{
    private const string ProtocolVersion = "2025-06-18";
    private string? _sessionId;
    private int _nextId;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await SendAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = ProtocolVersion,
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "eisenberg-worker", ["version"] = "0.1" }
        }, cancellationToken);
        using var request = CreateRequest(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" });
        using var response = await http.SendAsync(request, cancellationToken);
    }

    public async Task<JsonArray> ListToolsAsync(CancellationToken cancellationToken)
    {
        var result = await SendAsync("tools/list", new JsonObject(), cancellationToken);
        return Json.Array(result?["tools"]);
    }

    public async Task<McpToolResult> CallToolAsync(string name, JsonObject arguments, CancellationToken cancellationToken)
    {
        var result = await SendAsync("tools/call", new JsonObject { ["name"] = name, ["arguments"] = arguments }, cancellationToken);
        return McpToolResult.From(result);
    }

    private async Task<JsonNode?> SendAsync(string method, JsonObject parameters, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        using var request = CreateRequest(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters });
        using var response = await http.SendAsync(request, cancellationToken);
        if (response.Headers.TryGetValues("Mcp-Session-Id", out var values))
            _sessionId = values.FirstOrDefault() ?? _sessionId;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new McpException($"{method} returned {(int)response.StatusCode}: {Json.Trim(body)}");
        var message = response.Content.Headers.ContentType?.MediaType == "text/event-stream"
            ? FromEventStream(body, id)
            : JsonNode.Parse(body);
        if (message?["error"] is JsonNode error)
            throw new McpException($"{method} failed: {Json.Str(error["message"])}");
        return message?["result"];
    }

    private HttpRequestMessage CreateRequest(JsonObject payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", ProtocolVersion);
        if (_sessionId is not null)
            request.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId);
        return request;
    }

    private static JsonNode? FromEventStream(string body, int id)
    {
        var data = new StringBuilder();
        foreach (var raw in (body + "\n\n").Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                data.Append(line[5..].TrimStart());
                continue;
            }
            if (line.Length > 0 || data.Length == 0) continue;
            var node = JsonNode.Parse(data.ToString());
            data.Clear();
            if (node?["id"]?.ToJsonString() == id.ToString())
                return node;
        }
        return null;
    }
}
