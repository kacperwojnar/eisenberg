using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Eisenberg.Worker.Interpretation;

sealed class BrowserSession
{
    private static readonly string InventoryScript = Resource("inventory.js");
    private static readonly string CollectScript = Resource("collect-options.js");
    private static readonly string[] SkippedFrameHosts =
    [
        "doubleclick.net", "googlesyndication.com", "googleadservices.com", "adservice.google", "amazon-adsystem.com",
        "criteo", "taboola", "outbrain", "facebook.com/plugins", "googletagmanager.com"
    ];

    private const string InventoryTemplate = """
        const inventory = __INVENTORY__;
        const skipped = __SKIPPED__;
        const result = { url: page.url(), title: await page.title(), frames: [] };
        const frames = page.frames();
        for (let index = 0; index < frames.length; index++) {
          const frame = frames[index];
          const url = frame.url();
          if (index > 0 && (url === "about:blank" || skipped.some((host) => url.includes(host)))) continue;
          try {
            result.frames.push({ index, url, ...(await frame.evaluate(inventory, { frame: index, maxOptions: __MAX_OPTIONS__ })) });
          } catch (error) {
            result.frames.push({ index, url, error: String((error && error.message) || error) });
          }
        }
        return result;
        """;

    private const string CollectTemplate = """
        const collect = __COLLECT__;
        const frame = page.frames()[__FRAME__] || page.mainFrame();
        return await frame.evaluate(collect, { selector: __SELECTOR__ });
        """;

    private readonly McpClient _mcp;

    private BrowserSession(McpClient mcp, string id, string? liveViewUrl, JsonArray tools)
    {
        _mcp = mcp;
        Id = id;
        LiveViewUrl = liveViewUrl;
        Tools = tools;
    }

    public string Id { get; }
    public string? LiveViewUrl { get; }
    public JsonArray Tools { get; }

    public static async Task<BrowserSession> OpenAsync(McpClient mcp, CancellationToken cancellationToken)
    {
        await mcp.InitializeAsync(cancellationToken);
        var tools = await mcp.ListToolsAsync(cancellationToken);
        var created = await mcp.CallToolAsync("create_browser_session", new JsonObject(), cancellationToken);
        if (created.IsError)
            throw new McpException("create_browser_session failed: " + Json.Trim(created.Text));
        var data = created.Structured ?? Json.FirstValue(created.Text, node => node?["browserSessionId"] is not null);
        var id = Json.Str(data?["browserSessionId"]);
        if (id.Length == 0)
        {
            var match = Regex.Match(created.Text, @"browserSessionId\W+([\w\-:.]+)");
            id = match.Success ? match.Groups[1].Value : "";
        }
        if (id.Length == 0)
            throw new McpException("create_browser_session returned no session id: " + Json.Trim(created.Text));
        var liveView = Json.Str(data?["liveViewUrl"]);
        if (liveView.Length == 0)
        {
            var match = Regex.Match(created.Text, @"https://\S*live\S*", RegexOptions.IgnoreCase);
            liveView = match.Success ? match.Value.TrimEnd(')', '.', ',', '"') : "";
        }
        return new BrowserSession(mcp, id, liveView.Length == 0 ? null : liveView, tools);
    }

    public Task<McpToolResult> CallAsync(string tool, JsonObject arguments, CancellationToken cancellationToken)
    {
        arguments["browserSessionId"] = Id;
        return _mcp.CallToolAsync(tool, arguments, cancellationToken);
    }

    public async Task<JsonNode> InventoryAsync(int maxOptions, CancellationToken cancellationToken)
    {
        var code = InventoryTemplate
            .Replace("__INVENTORY__", InventoryScript)
            .Replace("__SKIPPED__", new JsonArray(SkippedFrameHosts.Select(host => (JsonNode?)host).ToArray()).ToJsonString())
            .Replace("__MAX_OPTIONS__", maxOptions.ToString());
        var result = await CallAsync("browser_run_code", new JsonObject { ["code"] = code }, cancellationToken);
        if (result.IsError)
            throw new McpException("The inventory script failed: " + Json.Trim(result.Text));
        return Unwrap(result, node => node?["frames"] is JsonArray)
            ?? throw new McpException("The inventory script returned no frames: " + Json.Trim(result.Text));
    }

    public async Task<JsonArray?> CollectOptionsAsync(int frame, string selector, CancellationToken cancellationToken)
    {
        var code = CollectTemplate
            .Replace("__COLLECT__", CollectScript)
            .Replace("__FRAME__", frame.ToString())
            .Replace("__SELECTOR__", JsonValue.Create(selector).ToJsonString());
        var result = await CallAsync("browser_run_code", new JsonObject { ["code"] = code }, cancellationToken);
        if (result.IsError) return null;
        return Unwrap(result, node => node?["options"] is JsonArray)?["options"] as JsonArray;
    }

    public async Task<McpImage?> ScreenshotAsync(CancellationToken cancellationToken)
    {
        var result = await CallAsync("browser_take_screenshot", new JsonObject { ["type"] = "jpeg" }, cancellationToken);
        return result.IsError ? null : result.Images.FirstOrDefault();
    }

    public async Task CloseAsync()
    {
        try
        {
            await _mcp.CallToolAsync("close_browser_session", new JsonObject { ["browserSessionId"] = Id }, CancellationToken.None);
        }
        catch (Exception)
        {
            // The session may already be gone; it expires on its own.
        }
    }

    private static JsonNode? Unwrap(McpToolResult result, Func<JsonNode?, bool> accept)
    {
        if (accept(result.Structured)) return result.Structured;
        if (accept(result.Structured?["result"])) return result.Structured!["result"];
        var parsed = Json.FirstValue(result.Text, node => accept(node) || accept(node?["result"]));
        return accept(parsed) ? parsed : parsed?["result"];
    }

    private static string Resource(string name)
    {
        using var stream = typeof(BrowserSession).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("Missing embedded script " + name);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
