using System.Text;
using System.Text.Json.Nodes;

namespace Eisenberg.Worker.Interpretation;

sealed record ToolSpec(string Name, string Description, JsonObject Schema);
sealed record ToolCall(string Id, string Name, JsonObject Arguments);
sealed record ToolResult(string CallId, string Content, bool IsError = false);

abstract record ChatItem;
sealed record UserText(string Text) : ChatItem;
sealed record AssistantTurn(string Text, IReadOnlyList<ToolCall> Calls) : ChatItem;
sealed record ToolResults(IReadOnlyList<ToolResult> Results, string? Note = null) : ChatItem;

sealed class ModelUsage
{
    public int Calls { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
}

interface IChatModel
{
    string Deployment { get; }
    ModelUsage Usage { get; }
    Task<AssistantTurn> NextAsync(string system, IReadOnlyList<ChatItem> history, IReadOnlyList<ToolSpec> tools, int maxTokens, CancellationToken cancellationToken);
    Task<string> LookAsync(string system, string text, string mediaType, string base64, CancellationToken cancellationToken);
}

static class ChatModels
{
    // Claude deployments on Foundry speak the Anthropic Messages API; the others speak the OpenAI API.
    public static IChatModel Create(HttpClient http, string endpoint, string apiKey, string deployment) =>
        deployment.StartsWith("claude", StringComparison.OrdinalIgnoreCase)
            ? new AnthropicModel(http, endpoint, apiKey, deployment)
            : new OpenAiModel(http, endpoint, apiKey, deployment);

    public static async Task<JsonNode> PostAsync(HttpClient http, string url, string apiKey, JsonObject body, string deployment,
        CancellationToken cancellationToken, params (string Name, string Value)[] headers)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation("api-key", apiKey);
            foreach (var (name, value) in headers)
                request.Headers.TryAddWithoutValidation(name, value);
            using var response = await http.SendAsync(request, cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.IsSuccessStatusCode)
                return JsonNode.Parse(text) ?? new JsonObject();
            var retry = (int)response.StatusCode is 429 or 500 or 502 or 503 or 529;
            if (!retry || attempt >= 4)
                throw new InvalidOperationException($"{deployment} returned {(int)response.StatusCode}: {Json.Trim(text)}");
            await Task.Delay(TimeSpan.FromSeconds(5 * attempt), cancellationToken);
        }
    }
}

sealed class AnthropicModel(HttpClient http, string endpoint, string apiKey, string deployment) : IChatModel
{
    public string Deployment => deployment;
    public ModelUsage Usage { get; } = new();

    public async Task<AssistantTurn> NextAsync(string system, IReadOnlyList<ChatItem> history, IReadOnlyList<ToolSpec> tools, int maxTokens, CancellationToken cancellationToken)
    {
        var messages = new JsonArray();
        foreach (var item in history)
        {
            switch (item)
            {
                case UserText user:
                    Append(messages, "user", new JsonObject { ["type"] = "text", ["text"] = user.Text });
                    break;
                case AssistantTurn turn:
                    var blocks = new List<JsonObject>();
                    if (turn.Text.Length > 0) blocks.Add(new JsonObject { ["type"] = "text", ["text"] = turn.Text });
                    blocks.AddRange(turn.Calls.Select(call => new JsonObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = call.Id,
                        ["name"] = call.Name,
                        ["input"] = call.Arguments.DeepClone()
                    }));
                    Append(messages, "assistant", blocks.ToArray());
                    break;
                case ToolResults results:
                    var parts = results.Results.Select(result => new JsonObject
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = result.CallId,
                        ["content"] = result.Content,
                        ["is_error"] = result.IsError
                    }).ToList();
                    if (results.Note is { Length: > 0 } note) parts.Add(new JsonObject { ["type"] = "text", ["text"] = note });
                    Append(messages, "user", parts.ToArray());
                    break;
            }
        }

        var body = new JsonObject
        {
            ["model"] = deployment,
            ["max_tokens"] = maxTokens,
            ["system"] = new JsonArray(new JsonObject
            {
                ["type"] = "text",
                ["text"] = system,
                ["cache_control"] = new JsonObject { ["type"] = "ephemeral" }
            }),
            ["messages"] = messages
        };
        if (tools.Count > 0)
        {
            body["tools"] = new JsonArray(tools.Select(tool => (JsonNode?)new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["input_schema"] = tool.Schema.DeepClone()
            }).ToArray());
        }

        var root = await ChatModels.PostAsync(http, endpoint.TrimEnd('/') + "/anthropic/v1/messages", apiKey, body, deployment,
            cancellationToken, ("anthropic-version", "2023-06-01"));
        Usage.Calls++;
        var usage = root["usage"];
        Usage.InputTokens += Json.Int(usage?["input_tokens"]) + Json.Int(usage?["cache_read_input_tokens"]) + Json.Int(usage?["cache_creation_input_tokens"]);
        Usage.OutputTokens += Json.Int(usage?["output_tokens"]);

        var text = new StringBuilder();
        var calls = new List<ToolCall>();
        foreach (var block in Json.Array(root["content"]))
        {
            var type = Json.Str(block?["type"]);
            if (type == "text") text.Append(Json.Str(block?["text"]));
            else if (type == "tool_use")
                calls.Add(new ToolCall(Json.Str(block?["id"]), Json.Str(block?["name"]), block?["input"]?.DeepClone() as JsonObject ?? new JsonObject()));
        }
        return new AssistantTurn(text.ToString(), calls);
    }

    public async Task<string> LookAsync(string system, string text, string mediaType, string base64, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["model"] = deployment,
            ["max_tokens"] = 800,
            ["system"] = system,
            ["messages"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray(
                    new JsonObject
                    {
                        ["type"] = "image",
                        ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = mediaType, ["data"] = base64 }
                    },
                    new JsonObject { ["type"] = "text", ["text"] = text })
            })
        };
        var root = await ChatModels.PostAsync(http, endpoint.TrimEnd('/') + "/anthropic/v1/messages", apiKey, body, deployment,
            cancellationToken, ("anthropic-version", "2023-06-01"));
        Usage.Calls++;
        Usage.InputTokens += Json.Int(root["usage"]?["input_tokens"]);
        Usage.OutputTokens += Json.Int(root["usage"]?["output_tokens"]);
        return string.Join("", Json.Array(root["content"]).Where(block => Json.Str(block?["type"]) == "text").Select(block => Json.Str(block?["text"])));
    }

    private static void Append(JsonArray messages, string role, params JsonObject[] blocks)
    {
        if (messages.Count > 0 && Json.Str(messages[messages.Count - 1]?["role"]) == role)
        {
            var content = (JsonArray)messages[messages.Count - 1]!["content"]!;
            foreach (var block in blocks) content.Add(block);
            return;
        }
        messages.Add(new JsonObject { ["role"] = role, ["content"] = new JsonArray(blocks.Select(block => (JsonNode?)block).ToArray()) });
    }
}

sealed class OpenAiModel(HttpClient http, string endpoint, string apiKey, string deployment) : IChatModel
{
    public string Deployment => deployment;
    public ModelUsage Usage { get; } = new();

    public async Task<AssistantTurn> NextAsync(string system, IReadOnlyList<ChatItem> history, IReadOnlyList<ToolSpec> tools, int maxTokens, CancellationToken cancellationToken)
    {
        var messages = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = system });
        foreach (var item in history)
        {
            switch (item)
            {
                case UserText user:
                    messages.Add(new JsonObject { ["role"] = "user", ["content"] = user.Text });
                    break;
                case AssistantTurn turn:
                    var message = new JsonObject { ["role"] = "assistant", ["content"] = turn.Text.Length > 0 ? turn.Text : null };
                    if (turn.Calls.Count > 0)
                    {
                        message["tool_calls"] = new JsonArray(turn.Calls.Select(call => (JsonNode?)new JsonObject
                        {
                            ["id"] = call.Id,
                            ["type"] = "function",
                            ["function"] = new JsonObject { ["name"] = call.Name, ["arguments"] = call.Arguments.ToJsonString() }
                        }).ToArray());
                    }
                    messages.Add(message);
                    break;
                case ToolResults results:
                    foreach (var result in results.Results)
                        messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = result.CallId, ["content"] = result.Content });
                    if (results.Note is { Length: > 0 } note)
                        messages.Add(new JsonObject { ["role"] = "user", ["content"] = note });
                    break;
            }
        }

        var body = new JsonObject
        {
            ["model"] = deployment,
            ["messages"] = messages,
            ["max_completion_tokens"] = maxTokens
        };
        if (tools.Count > 0)
        {
            body["tools"] = new JsonArray(tools.Select(tool => (JsonNode?)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = tool.Schema.DeepClone()
                }
            }).ToArray());
        }

        var root = await ChatModels.PostAsync(http, endpoint.TrimEnd('/') + "/openai/v1/chat/completions", apiKey, body, deployment, cancellationToken);
        Usage.Calls++;
        Usage.InputTokens += Json.Int(root["usage"]?["prompt_tokens"]);
        Usage.OutputTokens += Json.Int(root["usage"]?["completion_tokens"]);

        var reply = root["choices"]?[0]?["message"];
        var calls = Json.Array(reply?["tool_calls"]).Select(call =>
        {
            JsonObject arguments;
            try { arguments = JsonNode.Parse(Json.Str(call?["function"]?["arguments"])) as JsonObject ?? new JsonObject(); }
            catch (System.Text.Json.JsonException) { arguments = new JsonObject(); }
            return new ToolCall(Json.Str(call?["id"]), Json.Str(call?["function"]?["name"]), arguments);
        }).ToList();
        return new AssistantTurn(reply?["content"] is null ? "" : Json.Str(reply["content"]), calls);
    }

    public async Task<string> LookAsync(string system, string text, string mediaType, string base64, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["model"] = deployment,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray(
                        new JsonObject { ["type"] = "text", ["text"] = text },
                        new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = $"data:{mediaType};base64,{base64}" } })
                }),
            ["max_completion_tokens"] = 800
        };
        var root = await ChatModels.PostAsync(http, endpoint.TrimEnd('/') + "/openai/v1/chat/completions", apiKey, body, deployment, cancellationToken);
        Usage.Calls++;
        Usage.InputTokens += Json.Int(root["usage"]?["prompt_tokens"]);
        Usage.OutputTokens += Json.Int(root["usage"]?["completion_tokens"]);
        var content = root["choices"]?[0]?["message"]?["content"];
        return content is null ? "" : Json.Str(content);
    }
}
