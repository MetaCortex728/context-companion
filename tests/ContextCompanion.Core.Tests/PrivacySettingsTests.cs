using ContextCompanion.Core.Privacy;

namespace ContextCompanion.Core.Tests;

public class PrivacySettingsTests
{
    [Fact]
    public void DefaultsKeepOnlyProcessNameCaptureModeRegionAndScale()
    {
        var metadata = PrivacySettings.Default.Metadata;
        Assert.True(metadata.ProcessName);
        Assert.True(metadata.CaptureMode);
        Assert.True(metadata.Region);
        Assert.True(metadata.Scale);
        Assert.False(metadata.WindowTitle);
        Assert.False(metadata.ExecutablePath);
        Assert.False(metadata.WindowClass);
        Assert.Empty(PrivacySettings.Default.Validate());
    }

    [Fact]
    public void MinimalFileGivesTheDefaults()
    {
        var settings = PrivacySettings.Parse("""{"schema_version":1}""");
        Assert.Equal(PrivacySettings.Default.ToJson(), settings.ToJson());
    }

    [Fact]
    public void JsonRoundTripsAndFieldsSwitchIndividually()
    {
        var settings = PrivacySettings.Parse("""
            {"schema_version":1,"metadata":{"window_title":true,"process_name":false}}
            """);
        Assert.True(settings.Metadata.WindowTitle);
        Assert.False(settings.Metadata.ProcessName);
        Assert.True(settings.Metadata.CaptureMode);
        var again = PrivacySettings.Parse(settings.ToJson());
        Assert.Equal(settings.Metadata, again.Metadata);
        Assert.Contains("\"window_title\": true", settings.ToJson(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"schema_version":2}""")]
    [InlineData("""{"schema_version":1,"unknown":true}""")]
    [InlineData("""{"schema_version":1,"metadata":{"window_titel":true}}""")]
    [InlineData("""{"schema_version":1,"metadata":{"window_title":"yes"}}""")]
    [InlineData("""{"schema_version":1,"metadata":null}""")]
    [InlineData("""not json""")]
    [InlineData("""null""")]
    public void InvalidSettingsAreRefused(string json) =>
        Assert.Throws<PrivacySettingsException>(() => PrivacySettings.Parse(json));

    [Fact]
    public void MissingFileGivesTheDefaults()
    {
        using var temp = new TempDirectory();
        Assert.Equal(PrivacySettings.Default.ToJson(), PrivacySettings.LoadOrDefault(temp.Combine("privacy.json")).ToJson());
        File.WriteAllText(temp.Combine("privacy.json"), """{"schema_version":1,"metadata":{"scale":false}}""");
        Assert.False(PrivacySettings.LoadOrDefault(temp.Combine("privacy.json")).Metadata.Scale);
    }
}
