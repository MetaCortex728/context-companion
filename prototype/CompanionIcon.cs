using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DomoCompanion;

internal static class CompanionIcon
{
    internal static System.Drawing.Icon LoadTrayIcon()
    {
        using var stream = typeof(CompanionIcon).Assembly.GetManifestResourceStream("DomoCompanion.Assets.companion.ico")
            ?? throw new InvalidOperationException("Companion icon resource is missing.");
        using var icon = new System.Drawing.Icon(stream, System.Windows.Forms.SystemInformation.SmallIconSize);
        return (System.Drawing.Icon)icon.Clone();
    }
    internal static ImageSource LoadWindowIcon()
    {
        using var stream = typeof(CompanionIcon).Assembly.GetManifestResourceStream("DomoCompanion.Assets.companion.ico")
            ?? throw new InvalidOperationException("Companion icon resource is missing.");
        return BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
    }
}
