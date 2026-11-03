using System.Text.Json;
using System.Text.RegularExpressions;
using ContextCompanion.Core.Packets;

namespace ContextCompanion.Core.Tests;

/// <summary>Keeps schema/*.schema.json and the validator in agreement.</summary>
public class SchemaFileTests
{
    private static string RepositoryFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ContextCompanion.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine([directory.FullName, .. parts]);
    }

    private static JsonElement Load(string name) =>
        JsonDocument.Parse(File.ReadAllText(RepositoryFile("schema", name))).RootElement.Clone();

    private static HashSet<string> Keys(JsonElement properties) => properties.EnumerateObject().Select(p => p.Name).ToHashSet();
    private static HashSet<string> Required(JsonElement schema) =>
        schema.TryGetProperty("required", out var r) ? r.EnumerateArray().Select(e => e.GetString()!).ToHashSet() : [];

    [Fact]
    public void PacketSchemaListsTheSamePropertiesAsTheValidator()
    {
        var schema = Load("packet.schema.json");
        var defs = schema.GetProperty("$defs");
        Assert.Equal(PacketFormat.FormatName, schema.GetProperty("properties").GetProperty("format").GetProperty("const").GetString());
        Assert.Equal(PacketFormat.SchemaVersion, schema.GetProperty("properties").GetProperty("schema_version").GetProperty("const").GetInt32());
        Assert.Equal(PacketValidator.ManifestKeys, Keys(schema.GetProperty("properties")));
        Assert.Equal(PacketValidator.ManifestRequired, Required(schema));
        Assert.Equal(PacketValidator.ItemKeys, Keys(defs.GetProperty("item").GetProperty("properties")));
        Assert.Equal(PacketValidator.ItemKeys, Required(defs.GetProperty("item")));
        Assert.Equal(PacketValidator.SourceKeys, Keys(defs.GetProperty("source").GetProperty("properties")));
        Assert.Equal(PacketValidator.RegionKeys, Keys(defs.GetProperty("source").GetProperty("properties").GetProperty("region").GetProperty("properties")));
        Assert.Equal(PacketValidator.RedactionKeys, Keys(defs.GetProperty("redaction").GetProperty("properties")));
        Assert.Equal(PacketValidator.RedactionRequired, Required(defs.GetProperty("redaction")));
        Assert.Equal(PacketFormat.MaxItems, schema.GetProperty("properties").GetProperty("items").GetProperty("maxItems").GetInt32());
        Assert.Equal(PacketFormat.MaxPacketBytes, defs.GetProperty("item").GetProperty("properties").GetProperty("bytes").GetProperty("maximum").GetInt64());
    }

    [Fact]
    public void AckSchemaListsTheSamePropertiesAsTheValidator()
    {
        var schema = Load("ack.schema.json");
        Assert.Equal(PacketFormat.AckFormatName, schema.GetProperty("properties").GetProperty("format").GetProperty("const").GetString());
        Assert.Equal(PacketValidator.AckKeys, Keys(schema.GetProperty("properties")));
        Assert.Equal(PacketValidator.AckRequired, Required(schema));
    }

    [Theory]
    [InlineData("capture.png")]
    [InlineData("a")]
    [InlineData("my_file-2.v1.txt")]
    [InlineData("../escape.png")]
    [InlineData("a/b.png")]
    [InlineData(".hidden")]
    [InlineData("x..png")]
    [InlineData("trailing.")]
    [InlineData("space name.png")]
    [InlineData("café.png")]
    [InlineData("C:x.png")]
    public void SchemaFileNamePatternAgreesWithTheValidator(string name)
    {
        string pattern = Load("packet.schema.json").GetProperty("$defs").GetProperty("fileName").GetProperty("pattern").GetString()!;
        Assert.Equal(PacketNames.IsValidItemFileName(name), Regex.IsMatch(name, pattern));
    }

    [Theory]
    [InlineData("01ARYZ6S41TSV4RRFFQ69G5FAV")]
    [InlineData("01aryz6s41tsv4rrffq69g5fav")]
    [InlineData("81ARYZ6S41TSV4RRFFQ69G5FAV")]
    [InlineData("01ARYZ6S41TSV4RRFFQ69G5FAU")]
    [InlineData("01ARYZ6S41TSV4RRFFQ69G5FA")]
    public void SchemaUlidPatternAgreesWithTheValidator(string id)
    {
        string pattern = Load("packet.schema.json").GetProperty("$defs").GetProperty("ulid").GetProperty("pattern").GetString()!;
        Assert.Equal(Ulid.IsValid(id), Regex.IsMatch(id, pattern));
    }
}
