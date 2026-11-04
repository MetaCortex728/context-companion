using ContextCompanion.Core.Capturing;
using ContextCompanion.Core.Privacy;

namespace ContextCompanion.Core.Tests;

public class ImageOpsTests
{
    /// <summary>A tightly packed image from BGRA quadruples, row by row.</summary>
    private static BgraImage Image(int width, int height, params byte[] bgra) => new(width, height, width * 4, bgra);

    [Fact]
    public void CropCopiesExactlyTheRectangleAndDropsRowPadding()
    {
        // Pattern pixel (x, y) = B 10+x, G 20+y, R 30+x+y, A 200, with 2 padding bytes per row.
        var source = TestImages.Pattern(4, 3, padding: 2);
        var before = source.Pixels.ToArray();

        var cropped = ImageOps.Crop(source, new ScreenRect(1, 1, 2, 2));

        Assert.Equal(2, cropped.Width);
        Assert.Equal(2, cropped.Height);
        Assert.Equal(8, cropped.Stride);
        Assert.Equal(new byte[]
        {
            11, 21, 32, 200,   12, 21, 33, 200,
            11, 22, 33, 200,   12, 22, 34, 200,
        }, cropped.Pixels);
        Assert.Equal(before, source.Pixels);
    }

    [Theory]
    [InlineData(-1, 0, 2, 2)]
    [InlineData(0, 0, 5, 1)]
    [InlineData(3, 2, 2, 1)]
    [InlineData(0, 0, 0, 1)]
    public void CropRefusesRectanglesOutsideTheImage(int x, int y, int w, int h) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageOps.Crop(TestImages.Pattern(4, 3), new ScreenRect(x, y, w, h)));

    [Fact]
    public void TwoToOneAveragesEachBlockWeightingColourByAlpha()
    {
        var source = Image(4, 2,
            // row 0: transparent red, opaque blue | four opaque colours for the second block
            0, 0, 255, 0,     255, 0, 0, 255,     10, 20, 30, 255,    20, 40, 60, 255,
            // row 1
            255, 0, 0, 255,   255, 0, 0, 255,     30, 60, 90, 255,    42, 81, 121, 255);

        var result = ImageOps.Downscale(source, 2);

        Assert.Equal((2, 1), (result.Width, result.Height));
        Assert.Equal(new byte[]
        {
            // The transparent red pixel adds no colour, only a quarter of transparency: 765 / 4 = 191.25.
            255, 0, 0, 191,
            // (10+20+30+42)/4 = 25.5 -> 26, (20+40+60+81)/4 = 50.25 -> 50, (30+60+90+121)/4 = 75.25 -> 75.
            26, 50, 75, 255,
        }, result.Pixels);
    }

    [Fact]
    public void NonIntegerDownscaleWeightsPartlyCoveredPixels()
    {
        // 3 x 3 -> 2 x 2: along each axis output 0 covers 1 source pixel and a third of the next (weights 2:1 in thirds),
        // output 1 the remaining third and the last pixel (1:2).
        byte[] blue = [9, 18, 27, 36, 45, 54, 63, 72, 81];
        var pixels = blue.SelectMany(b => new byte[] { b, 0, 0, 255 }).ToArray();

        var result = ImageOps.Downscale(Image(3, 3, pixels), 2);

        Assert.Equal((2, 2), (result.Width, result.Height));
        Assert.Equal(new byte[]
        {
            21, 0, 0, 255,   33, 0, 0, 255,   // (4*9+2*18+2*36+45)/9 = 21, (2*18+4*27+45+2*54)/9 = 33
            57, 0, 0, 255,   69, 0, 0, 255,   // (2*36+45+4*63+2*72)/9 = 57, (45+2*54+2*72+4*81)/9 = 69
        }, result.Pixels);
    }

    [Fact]
    public void NonIntegerDownscaleOfARowKeepsHeightOne()
    {
        var source = Image(3, 1, 0, 3, 0, 255, 90, 0, 0, 255, 255, 0, 0, 255);
        var result = ImageOps.Downscale(source, 2);
        Assert.Equal((2, 1), (result.Width, result.Height));
        // B: (2*0+90)/3 = 30 and (90+2*255)/3 = 200; G: (2*3+0)/3 = 2 and 0.
        Assert.Equal(new byte[] { 30, 2, 0, 255, 200, 0, 0, 255 }, result.Pixels);
    }

    [Fact]
    public void DownscaleToOneByOneAveragesEverything()
    {
        var source = Image(3, 2,
            0, 1, 2, 255,    10, 1, 2, 255,   20, 1, 2, 255,
            30, 1, 3, 255,   40, 1, 3, 255,   50, 1, 3, 255);
        var result = ImageOps.Downscale(source, 1);
        Assert.Equal((1, 1), (result.Width, result.Height));
        // B 150/6 = 25, G 1, R 15/6 = 2.5 -> 3.
        Assert.Equal(new byte[] { 25, 1, 3, 255 }, result.Pixels);
    }

    [Fact]
    public void FullyTransparentAreaStaysTransparentBlack()
    {
        var source = Image(2, 1, 50, 60, 70, 0, 80, 90, 100, 0);
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, ImageOps.Downscale(source, 1).Pixels);
    }

    [Theory]
    [InlineData(0)]     // 0 keeps the original size
    [InlineData(4)]     // already fits exactly
    [InlineData(1568)]  // never enlarged
    public void DownscaleIsANoOpWhenTheImageFits(int maxLongEdge)
    {
        var source = TestImages.Pattern(4, 3, padding: 4);
        var before = source.Pixels.ToArray();
        var result = ImageOps.Downscale(source, maxLongEdge);
        Assert.Same(source, result);
        Assert.Equal(before, result.Pixels);
    }

    [Theory]
    [InlineData(1920, 1080, 1568, 1568, 882)]
    [InlineData(1000, 3000, 1568, 523, 1568)]  // 1000 * 1568 / 3000 = 522.67
    [InlineData(3, 2, 1, 1, 1)]
    [InlineData(10000, 3, 100, 100, 1)]        // the short edge never reaches 0
    [InlineData(800, 600, 0, 800, 600)]
    public void FitLongEdgeKeepsTheAspectRatio(int width, int height, int max, int expectedWidth, int expectedHeight) =>
        Assert.Equal((expectedWidth, expectedHeight), ImageOps.FitLongEdge(width, height, max));
}

public class CaptureSettingsTests
{
    [Fact]
    public void DefaultsAreDocumentedValues()
    {
        var capture = PrivacySettings.Default.Capture;
        Assert.Equal(1568, capture.MaxLongEdge);
        Assert.False(capture.IncludeCursor);
        Assert.True(capture.ExplicitCopy);
        Assert.Equal(500, capture.CopyTimeoutMs);
        Assert.Empty(capture.ExtraTerminalWindowClasses);
        Assert.Contains("\"capture\": {", PrivacySettings.Default.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void CaptureSectionIsReadFromTheSettingsFile()
    {
        var settings = PrivacySettings.Parse("""
            {"schema_version":1,"capture":{"max_long_edge":0,"include_cursor":true,"explicit_copy":false,"copy_timeout_ms":1200,
             "extra_terminal_window_classes":["MyTerm"]}}
            """);
        Assert.Equal(0, settings.Capture.MaxLongEdge);
        Assert.True(settings.Capture.IncludeCursor);
        Assert.False(settings.Capture.ExplicitCopy);
        Assert.Equal(TimeSpan.FromMilliseconds(1200), settings.Capture.CopyTimeout);
        Assert.True(settings.Capture.IsTerminalWindowClass("myterm"));
        Assert.Equal(settings.Capture, PrivacySettings.Parse(settings.ToJson()).Capture with { ExtraTerminalWindowClasses = settings.Capture.ExtraTerminalWindowClasses });
    }

    [Theory]
    [InlineData("""{"max_long_edge":8}""", "capture.max_long_edge")]
    [InlineData("""{"max_long_edge":20000}""", "capture.max_long_edge")]
    [InlineData("""{"copy_timeout_ms":10}""", "capture.copy_timeout_ms")]
    [InlineData("""{"extra_terminal_window_classes":[""]}""", "capture.extra_terminal_window_classes")]
    public void InvalidCaptureSettingsAreRefused(string capture, string problem)
    {
        var ex = Assert.Throws<PrivacySettingsException>(() => PrivacySettings.Parse($$"""{"schema_version":1,"capture":{{capture}}}"""));
        Assert.Contains(ex.Problems, p => p.StartsWith(problem, StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownCapturePropertiesAreRefused() =>
        Assert.Throws<PrivacySettingsException>(() => PrivacySettings.Parse("""{"schema_version":1,"capture":{"borderless":true}}"""));

    [Theory]
    [InlineData("ConsoleWindowClass")]
    [InlineData("CASCADIA_HOSTING_WINDOW_CLASS")]
    [InlineData("mintty")]
    [InlineData("MINTTY")]
    public void BuiltInTerminalClassesAreAlwaysListed(string windowClass)
    {
        var settings = new CaptureSettings { ExtraTerminalWindowClasses = [] };
        Assert.True(settings.IsTerminalWindowClass(windowClass));
        Assert.False(settings.IsTerminalWindowClass("Notepad"));
        Assert.False(settings.IsTerminalWindowClass(null));
    }
}
