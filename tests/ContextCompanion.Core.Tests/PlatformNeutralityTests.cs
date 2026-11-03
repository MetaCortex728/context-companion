using System.Reflection;

namespace ContextCompanion.Core.Tests;

public class PlatformNeutralityTests
{
    [Fact]
    public void CoreReferencesNoWindowsOnlyAssemblies()
    {
        var core = Assembly.Load("ContextCompanion.Core");
        var references = core.GetReferencedAssemblies().Select(a => a.Name ?? "").ToArray();
        Assert.DoesNotContain(references, name =>
            name.StartsWith("System.Windows", StringComparison.Ordinal) ||
            name.StartsWith("PresentationFramework", StringComparison.Ordinal) ||
            name.StartsWith("PresentationCore", StringComparison.Ordinal) ||
            name.StartsWith("WindowsBase", StringComparison.Ordinal) ||
            name.StartsWith("Microsoft.Win32", StringComparison.Ordinal));
    }
}
