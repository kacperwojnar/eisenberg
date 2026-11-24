using System.Text.Json;
using System.Text.Json.Nodes;
using Eisenberg.Data;
using Eisenberg.Worker.Interpretation;
using Microsoft.EntityFrameworkCore;

namespace Eisenberg.Worker.Instances;

sealed class InstanceBuilder(IServiceProvider services, IConfiguration configuration, IHttpClientFactory httpFactory, ILogger<InstanceBuilder> logger)
{
    public async Task BuildAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EisenbergDb>();
        var endpoint = configuration["FOUNDRY_ENDPOINT"];
        var apiKey = configuration["FOUNDRY_API_KEY"];
        var readings = await db.Files
            .Where(file => file.OrganizationId == organizationId && file.Reading != null && file.Reading != "")
            .Select(file => new { file.Id, file.OriginalFileName, file.Reading })
            .ToListAsync(cancellationToken);
        if (readings.Count == 0)
        {
            var generated = await db.Instances.Where(instance => instance.OrganizationId == organizationId && instance.Generated).ToListAsync(cancellationToken);
            db.Instances.RemoveRange(generated);
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        var modelIds = await db.FormPublications
            .Where(publication => publication.OrganizationId == organizationId)
            .Join(db.Forms, publication => publication.FormId, form => form.Id, (publication, form) => form.PublishedModelId)
            .Where(id => id != null)
            .Select(id => id!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(apiKey))
        {
            foreach (var modelId in modelIds)
                await BuildLocalAsync(db, organizationId, modelId, readings.Select(file => (file.Id, file.OriginalFileName, file.Reading!)).ToList(), cancellationToken);
            return;
        }

        var deployment = string.IsNullOrWhiteSpace(configuration["FOUNDRY_CHAT_DEPLOYMENT"]) ? "claude-haiku-5-5" : configuration["FOUNDRY_CHAT_DEPLOYMENT"]!;
        var http = httpFactory.CreateClient("interpretation");
        var model = ChatModels.Create(http, endpoint, apiKey, deployment);
        foreach (var modelId in modelIds)
            await BuildModelAsync(db, model, organizationId, modelId, readings.Select(file => (file.Id, file.OriginalFileName, file.Reading!)).ToList(), cancellationToken);
    }

    private async Task BuildModelAsync(EisenbergDb db, IChatModel model, Guid organizationId, Guid modelId, List<(Guid Id, string Name, string Text)> readings, CancellationToken cancellationToken)
    {
        var version = await db.ModelVersions.SingleOrDefaultAsync(item => item.Id == modelId, cancellationToken);
        if (version is null) return;
        var lists = await db.ChoiceLists.Where(list => list.ModelId == modelId).ToListAsync(cancellationToken);
        var documents = new JsonArray();
        var remaining = 120_000;
        foreach (var reading in readings)
        {
            if (remaining <= 0) break;
            var text = reading.Text.Length <= remaining ? reading.Text : reading.Text[..remaining];
            remaining -= text.Length;
            documents.Add(new JsonObject { ["name"] = reading.Name, ["text"] = text });
        }
        var input = new JsonObject
        {
            ["model"] = JsonNode.Parse(version.ModelJson),
            ["choiceLists"] = new JsonArray(lists.Select(list => (JsonNode?)new JsonObject
            {
                ["id"] = list.Id.ToString(),
                ["complete"] = list.IsComplete,
                ["choices"] = JsonNode.Parse(list.ChoicesJson)
            }).ToArray()),
            ["documents"] = documents
        };
        var history = new List<ChatItem> { new UserText(input.ToJsonString()) };
        JsonNode? answer = null;
        for (var attempt = 0; attempt < 2 && answer is null; attempt++)
        {
            var turn = await model.NextAsync(System, history, [], 16000, cancellationToken);
            answer = Json.FirstValue(turn.Text, node => node?["instances"] is JsonArray);
            if (answer is not null) break;
            history.Add(turn);
            history.Add(new UserText("Return only the JSON object with the instances array."));
        }
        if (answer is null)
        {
            logger.LogWarning("Instance builder returned no instances for model {ModelId}", modelId);
            return;
        }

        var runtime = FormRuntime.Parse(version.ModelJson, lists.Select(list => (list.Id.ToString(), list.IsComplete, list.ChoicesJson)));
        var stale = await db.Instances.Where(instance => instance.OrganizationId == organizationId && instance.ModelId == modelId && instance.Generated).ToListAsync(cancellationToken);
        db.Instances.RemoveRange(stale);
        var now = DateTimeOffset.UtcNow;
        foreach (var node in Json.Array(answer["instances"]))
        {
            if (node is not JsonObject item || item["values"] is not JsonObject valuesNode) continue;
            var typeName = Json.Str(item["type"]);
            var subclass = item["subclass"] is null || item["subclass"] is JsonValue { } empty && empty.ToJsonString() == "null" ? null : Json.Str(item["subclass"]);
            if (string.IsNullOrWhiteSpace(subclass)) subclass = null;
            var values = valuesNode.ToDictionary(pair => pair.Key, pair => (string?)pair.Value?.ToString());
            var problems = runtime.Validate(typeName, subclass, values);
            if (problems.Count > 0)
            {
                logger.LogWarning("Dropped a record for model {ModelId}: {Problems}", modelId, string.Join(" ", problems));
                continue;
            }
            db.Instances.Add(new FormInstance
            {
                Id = Guid.NewGuid(),
                OrganizationId = organizationId,
                ModelId = modelId,
                TypeName = typeName,
                SubclassName = subclass,
                Label = Json.Str(item["label"]),
                ValuesJson = valuesNode.ToJsonString(),
                SourcesJson = Sources(item["sources"] as JsonObject, readings),
                Generated = true,
                CreatedAt = now,
                UpdatedAt = now
            });
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task BuildLocalAsync(EisenbergDb db, Guid organizationId, Guid modelId, List<(Guid Id, string Name, string Text)> readings, CancellationToken cancellationToken)
    {
        var version = await db.ModelVersions.SingleOrDefaultAsync(item => item.Id == modelId, cancellationToken);
        if (version is null) return;
        var lists = await db.ChoiceLists.Where(list => list.ModelId == modelId).ToListAsync(cancellationToken);
        var built = LocalRecords.FromReadings(
            version.ModelJson,
            lists.Select(list => (list.Id.ToString(), list.IsComplete, list.ChoicesJson)),
            readings.Select(reading => (reading.Name, reading.Text)));
        var stale = await db.Instances.Where(instance => instance.OrganizationId == organizationId && instance.ModelId == modelId && instance.Generated).ToListAsync(cancellationToken);
        db.Instances.RemoveRange(stale);
        var now = DateTimeOffset.UtcNow;
        foreach (var record in built)
        {
            var sources = new JsonObject();
            foreach (var pair in record.Sources)
            {
                var file = readings.FirstOrDefault(reading => string.Equals(reading.Name, pair.Value.FileName, StringComparison.OrdinalIgnoreCase));
                var entry = new JsonObject
                {
                    ["fileName"] = pair.Value.FileName,
                    ["location"] = pair.Value.Location
                };
                if (pair.Value.Page is int page)
                    entry["page"] = page;
                if (file.Id != Guid.Empty)
                    entry["fileId"] = file.Id.ToString();
                sources[pair.Key] = entry;
            }
            db.Instances.Add(new FormInstance
            {
                Id = Guid.NewGuid(),
                OrganizationId = organizationId,
                ModelId = modelId,
                TypeName = record.TypeName,
                Label = record.Label,
                ValuesJson = JsonSerializer.Serialize(record.Values),
                SourcesJson = sources.ToJsonString(),
                Generated = true,
                CreatedAt = now,
                UpdatedAt = now
            });
        }
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Local extractor stored {Count} records for model {ModelId}", built.Count, modelId);
    }

    private const string System = """
        You build records of a form model from the readings of every document in the set. One record's values may come from different documents. Return one JSON object and nothing else:
        {"instances":[{"type":"TypeName","subclass":null,"label":"short name","values":{"propertyName":"value"},"sources":{"propertyName":{"document":"the document name","page":1,"location":"where on the page"}}}]}
        Use a type name from the model. subclass is the subclass name or null. For a property whose choice list is complete, the value is one of that list's stored keys, not the visible label. For an incomplete list, use the value as the documents state it. Omit a property when the documents do not contain it. sources names the document, page, and place for each value that the reading locates. Omit a source when the reading does not say where the value is. Return every record that can be built.
        """;

    private static string Sources(JsonObject? sources, List<(Guid Id, string Name, string Text)> readings)
    {
        if (sources is null) return "{}";
        var stored = new JsonObject();
        foreach (var pair in sources)
        {
            if (pair.Value is not JsonObject source) continue;
            var document = Json.Str(source["document"]);
            var file = readings.FirstOrDefault(reading => string.Equals(reading.Name, document, StringComparison.OrdinalIgnoreCase));
            var entry = new JsonObject
            {
                ["fileName"] = document,
                ["location"] = Location(source["location"])
            };
            if (Page(source["page"]) is int page)
                entry["page"] = page;
            if (file.Id != Guid.Empty)
                entry["fileId"] = file.Id.ToString();
            stored[pair.Key] = entry;
        }
        return stored.ToJsonString();
    }

    private static int? Page(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<int>(out var page)) return page;
        return value.TryGetValue<double>(out var number) ? (int)number : null;
    }

    private static string Location(JsonNode? node) => node switch
    {
        null => "",
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        _ => node.ToJsonString()
    };
}
