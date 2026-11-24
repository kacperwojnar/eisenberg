using Eisenberg.Data;
using Xunit;

namespace Eisenberg.Tests;

public class LocalRecordsTests
{
    private const string Model = """
    {
      "types": [
        {
          "name": "Driver",
          "properties": [
            { "name": "name", "kind": "text", "required": true, "controls": [{ "pageText": "Name", "selector": "#name" }] },
            { "name": "start", "kind": "date", "required": true, "controls": [{ "pageText": "Start", "selector": "#start" }] },
            { "name": "fuel", "kind": "choice", "required": true, "choiceListId": "fuel", "controls": [{ "pageText": "Fuel", "selector": "#fuel" }] }
          ]
        }
      ]
    }
    """;

    private const string Reading = """
    {"text":"Name: Ada Lovelace\nStart: 1990-01-02\nFuel: Benzyna","segments":[{"page":1,"text":"Name: Ada Lovelace","location":"local text"}]}
    """;

    [Fact]
    public void A_text_reading_becomes_one_record_of_the_published_type()
    {
        var records = LocalRecords.FromReadings(Model, [("fuel", true, """{"choices":[{"value":"petrol","text":"Benzyna"}]}""")], [("policy.txt", Reading)]);
        var record = Assert.Single(records);
        Assert.Equal("Driver", record.TypeName);
        Assert.Equal("Ada Lovelace", record.Values["name"]);
        Assert.Equal("1990-01-02", record.Values["start"]);
        Assert.Equal("petrol", record.Values["fuel"]);
        Assert.Equal("policy.txt", record.Sources["name"].FileName);
        Assert.Equal(1, record.Sources["fuel"].Page);
    }

    [Fact]
    public void A_reading_that_misses_a_required_value_builds_no_record()
    {
        var reading = """{"text":"Name: Ada Lovelace"}""";
        Assert.Empty(LocalRecords.FromReadings(Model, [("fuel", true, """{"choices":[{"value":"petrol","text":"Benzyna"}]}""")], [("policy.txt", reading)]));
    }
}
