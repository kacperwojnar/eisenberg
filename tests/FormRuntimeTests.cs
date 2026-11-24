using Eisenberg.Data;
using Xunit;

namespace Eisenberg.Tests;

public class FormRuntimeTests
{
    private const string Model = """
    {
      "types": [
        {
          "name": "Party",
          "subclassOf": null,
          "repeated": false,
          "properties": [
            { "name": "name", "kind": "text", "required": true, "shownWhen": "", "fillAfter": [], "ignored": false, "controls": [{ "key": "n", "pageText": "Name", "selector": "#name", "frame": 0, "step": 1 }] }
          ]
        },
        {
          "name": "Owner",
          "subclassOf": "Party",
          "repeated": false,
          "properties": [
            { "name": "fuel", "kind": "choice", "required": true, "shownWhen": "", "fillAfter": ["name"], "ignored": false, "choiceListId": "fuel", "controls": [{ "key": "f", "pageText": "Fuel", "selector": "#fuel", "frame": 0, "step": 2 }] },
            { "name": "note", "kind": "text", "required": false, "shownWhen": "leasing", "fillAfter": [], "ignored": true, "controls": [] }
          ]
        }
      ]
    }
    """;

    private static FormRuntime Runtime() => FormRuntime.Parse(Model, [("fuel", true, """{"choices":[{"value":"petrol","text":"Benzyna"}]}""")]);

    [Fact]
    public void Subclass_inherits_parent_properties()
    {
        var names = Runtime().PropertiesOf("Party", "Owner").Select(property => property.Name).ToArray();
        Assert.Contains("name", names);
        Assert.Contains("fuel", names);
    }

    [Fact]
    public void Complete_choice_must_be_a_stored_key_or_label()
    {
        var runtime = Runtime();
        Assert.Empty(runtime.Validate("Party", "Owner", new Dictionary<string, string?> { ["name"] = "Ada", ["fuel"] = "petrol" }));
        Assert.Empty(runtime.Validate("Party", "Owner", new Dictionary<string, string?> { ["name"] = "Ada", ["fuel"] = "Benzyna" }));
        Assert.Contains("fuel is not one of the stored choices.", runtime.Validate("Party", "Owner", new Dictionary<string, string?> { ["name"] = "Ada", ["fuel"] = "kerosene" }));
    }

    [Fact]
    public void Required_property_without_a_value_is_rejected()
    {
        var problems = Runtime().Problems("Party", null, new Dictionary<string, string?>());
        var problem = Assert.Single(problems);
        Assert.Equal("name", problem.Property);
        Assert.Equal("name is required.", problem.Message);
    }

    [Fact]
    public void Ignored_property_is_left_out_of_the_fill_plan()
    {
        var plan = Runtime().Plan("Party", "Owner", new Dictionary<string, string?> { ["name"] = "Ada", ["fuel"] = "petrol", ["note"] = "secret" });
        Assert.Equal(["name", "fuel"], plan.Select(step => step.Property).ToArray());
        Assert.Equal("Benzyna", plan[1].Value);
    }

    [Fact]
    public void Fill_order_follows_fill_after()
    {
        var plan = Runtime().Plan("Party", "Owner", new Dictionary<string, string?> { ["name"] = "Ada", ["fuel"] = "petrol" });
        Assert.Equal("name", plan[0].Property);
        Assert.Equal("fuel", plan[1].Property);
    }
}
