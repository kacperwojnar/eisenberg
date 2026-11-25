using System.Text.Json;
using Eisenberg.Data;

if (args.Length != 1 || !Directory.Exists(args[0]))
{
    Console.Error.WriteLine("Usage: Eisenberg.Pack <pack-folder>");
    return 1;
}

var folder = args[0];
var model = File.ReadAllText(Path.Combine(folder, "model.json"));
var choicesPath = Path.Combine(folder, "choices.json");
var lists = File.Exists(choicesPath)
    ? JsonSerializer.Deserialize<List<ChoiceFile>>(File.ReadAllText(choicesPath), JsonSerializerOptions.Web) ?? []
    : [];
var documents = Directory.GetFiles(Path.Combine(folder, "documents"))
    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
    .Select(path => (Name: Path.GetFileName(path), Text: File.ReadAllText(path)))
    .ToList();
var records = LocalRecords.FromReadings(
    model,
    lists.Select(list => (list.Id, list.Complete, JsonSerializer.Serialize(list.Choices, JsonSerializerOptions.Web))),
    documents.Select(document => (document.Name, document.Text)));
var payload = records.Select(record => new
{
    type = record.TypeName,
    label = record.Label,
    values = record.Values,
    sources = record.Sources.ToDictionary(
        pair => pair.Key,
        pair => new { fileName = pair.Value.FileName, page = pair.Value.Page, location = pair.Value.Location })
});
Console.WriteLine(JsonSerializer.Serialize(payload));
return 0;

sealed class ChoiceFile
{
    public string Id { get; set; } = "";
    public bool Complete { get; set; }
    public JsonElement Choices { get; set; }
}
