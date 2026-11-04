using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ContextCompanion.Core.Capturing;
using static ContextCompanion.Platform.Windows.Interop.NativeMethods;

namespace ContextCompanion.Platform.Windows.Text;

/// <summary>
/// The Windows clipboard through the Win32 API (no OLE, so any thread may use it).
/// <para>
/// <see cref="Save"/> keeps the formats that can be restored faithfully: the standard memory formats
/// (<c>CF_UNICODETEXT</c>, <c>CF_TEXT</c>, <c>CF_OEMTEXT</c>, <c>CF_LOCALE</c>, <c>CF_DIB</c>, <c>CF_DIBV5</c>,
/// <c>CF_HDROP</c>) and registered formats (for example <c>HTML Format</c>, <c>Rich Text Format</c>, <c>PNG</c>), all as
/// copies of their memory blocks, up to <see cref="MaxSnapshotBytes"/> in total. Formats held as GDI handles (bitmaps,
/// metafiles, palettes), private and owner-display formats, and OLE's own data-object formats cannot be copied and are
/// lost by a restore. See docs/capture.md.
/// </para>
/// <para>
/// Every write (<see cref="Restore"/>, <see cref="WriteText"/>) also sets <c>ExcludeClipboardContentFromMonitorProcessing</c>,
/// <c>CanIncludeInClipboardHistory</c> = 0 and <c>CanUploadToCloudClipboard</c> = 0, so nothing the tool puts on the
/// clipboard reaches clipboard history, the cloud clipboard or clipboard monitors.
/// </para>
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class WindowsClipboard : IClipboardAccess
{
    public const long MaxSnapshotBytes = 64L * 1024 * 1024;
    public const string ExcludeFromMonitoringFormat = "ExcludeClipboardContentFromMonitorProcessing";
    public const string HistoryFormat = "CanIncludeInClipboardHistory";
    public const string CloudFormat = "CanUploadToCloudClipboard";
    /// <summary>Added by a restore of content that was not marked sensitive, so a later read can tell it apart.</summary>
    public const string RestoredMarkerFormat = "ContextCompanion.RestoredClipboard";

    private const uint CF_TEXT = 1, CF_OEMTEXT = 7, CF_DIB = 8, CF_HDROP = 15, CF_LOCALE = 16, CF_DIBV5 = 17;
    private static readonly uint[] StandardMemoryFormats = [CF_UNICODETEXT, CF_TEXT, CF_OEMTEXT, CF_LOCALE, CF_DIB, CF_DIBV5, CF_HDROP];
    private static readonly string[] OleFormats = ["DataObject", "Ole Private Data", "Object Descriptor", "Link Source Descriptor", "Embed Source", "Link Source"];

    /// <summary>How long to keep trying when another program has the clipboard open.</summary>
    public static TimeSpan OpenTimeout { get; } = TimeSpan.FromMilliseconds(500);

    public uint SequenceNumber => GetClipboardSequenceNumber();

    public ClipboardText ReadText()
    {
        using var open = Open(IntPtr.Zero);
        bool sensitive = IsMarkedSensitive();
        if (sensitive) return new ClipboardText(null, true);
        IntPtr handle = GetClipboardData(CF_UNICODETEXT);
        return new ClipboardText(handle == IntPtr.Zero ? null : ReadUnicode(handle), false);
    }

    public IClipboardSnapshot Save()
    {
        using var open = Open(IntPtr.Zero);
        var formats = new List<(uint Format, byte[] Data)>();
        long total = 0;
        bool truncated = false;
        for (uint format = EnumClipboardFormats(0); format != 0; format = EnumClipboardFormats(format))
        {
            if (!IsRestorable(format)) continue;
            IntPtr handle = GetClipboardData(format);
            if (handle == IntPtr.Zero) continue;
            long size = (long)GlobalSize(handle).ToUInt64();
            if (size == 0) continue;
            if (total + size > MaxSnapshotBytes) { truncated = true; continue; }
            IntPtr pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero) continue;
            try
            {
                var data = new byte[size];
                Marshal.Copy(pointer, data, 0, (int)size);
                formats.Add((format, data));
                total += size;
            }
            finally { GlobalUnlock(handle); }
        }
        return new Snapshot(formats, IsMarkedSensitive(), truncated);
    }

    public void Restore(IClipboardSnapshot snapshot)
    {
        if (snapshot is not Snapshot saved) throw new ArgumentException("Not a snapshot of this clipboard.", nameof(snapshot));
        Write(saved.Formats, markRestored: !saved.WasSensitive);
    }

    /// <summary>Puts text on the clipboard, with the history and monitoring exclusions. Used by tests and later by the app.</summary>
    public void WriteText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var bytes = new byte[(text.Length + 1) * 2];
        System.Text.Encoding.Unicode.GetBytes(text, 0, text.Length, bytes, 0);
        Write([(CF_UNICODETEXT, bytes)], markRestored: false);
    }

    /// <summary>The names of the registered formats currently on the clipboard, with their first four bytes as a number.</summary>
    public IReadOnlyDictionary<string, uint?> ExclusionFormats()
    {
        using var open = Open(IntPtr.Zero);
        var result = new Dictionary<string, uint?>(StringComparer.Ordinal);
        foreach (string name in new[] { ExcludeFromMonitoringFormat, HistoryFormat, CloudFormat, RestoredMarkerFormat })
        {
            uint format = RegisterClipboardFormat(name);
            if (format == 0 || !IsClipboardFormatAvailable(format)) continue;
            IntPtr handle = GetClipboardData(format);
            result[name] = handle == IntPtr.Zero || (long)GlobalSize(handle).ToUInt64() < 4 ? null : ReadUInt32(handle);
        }
        return result;
    }

    private void Write(IReadOnlyList<(uint Format, byte[] Data)> formats, bool markRestored)
    {
        // An owner window is needed: after OpenClipboard(NULL), EmptyClipboard leaves the clipboard ownerless and
        // SetClipboardData may fail. A message-only STATIC window needs no class registration and is destroyed at once.
        IntPtr owner = CreateWindowEx(0, "STATIC", null, 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (owner == IntPtr.Zero) throw new TextSourceException("could not create the clipboard owner window");
        try
        {
            using var open = Open(owner);
            if (!EmptyClipboard()) throw new TextSourceException("the clipboard could not be emptied");
            foreach (var (format, data) in formats) Set(format, data);
            Set(RegisterClipboardFormat(ExcludeFromMonitoringFormat), BitConverter.GetBytes(0u));
            Set(RegisterClipboardFormat(HistoryFormat), BitConverter.GetBytes(0u));
            Set(RegisterClipboardFormat(CloudFormat), BitConverter.GetBytes(0u));
            if (markRestored) Set(RegisterClipboardFormat(RestoredMarkerFormat), BitConverter.GetBytes(1u));
        }
        finally { DestroyWindow(owner); }
    }

    private static void Set(uint format, byte[] data)
    {
        if (format == 0) throw new TextSourceException("a clipboard format could not be registered");
        IntPtr memory = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)(ulong)Math.Max(1, data.Length));
        if (memory == IntPtr.Zero) throw new TextSourceException("out of memory while writing the clipboard");
        IntPtr pointer = GlobalLock(memory);
        if (pointer == IntPtr.Zero) { GlobalFree(memory); throw new TextSourceException("out of memory while writing the clipboard"); }
        Marshal.Copy(data, 0, pointer, data.Length);
        GlobalUnlock(memory);
        // On success the system owns the memory; on failure it is still ours to free.
        if (SetClipboardData(format, memory) == IntPtr.Zero)
        {
            GlobalFree(memory);
            throw new TextSourceException("the clipboard could not be written");
        }
    }

    /// <summary>
    /// True when the content carries the sensitivity marks password managers set
    /// (<c>ExcludeClipboardContentFromMonitorProcessing</c>, or <c>CanIncludeInClipboardHistory</c> = 0) and was not
    /// put back by this tool's own restore of unmarked content.
    /// </summary>
    private static bool IsMarkedSensitive()
    {
        if (IsAvailable(RestoredMarkerFormat)) return false;
        if (IsAvailable(ExcludeFromMonitoringFormat)) return true;
        uint history = RegisterClipboardFormat(HistoryFormat);
        if (history == 0 || !IsClipboardFormatAvailable(history)) return false;
        IntPtr handle = GetClipboardData(history);
        return handle != IntPtr.Zero && (long)GlobalSize(handle).ToUInt64() >= 4 && ReadUInt32(handle) == 0;
    }

    private static bool IsAvailable(string name)
    {
        uint format = RegisterClipboardFormat(name);
        return format != 0 && IsClipboardFormatAvailable(format);
    }

    private static bool IsRestorable(uint format)
    {
        if (StandardMemoryFormats.Contains(format)) return true;
        if (format < 0xC000) return false; // GDI handles, private (0x200-0x2FF), GDI object (0x300-0x3FF), owner display
        var name = new char[256];
        int length = GetClipboardFormatName(format, name, name.Length);
        return length > 0 && !OleFormats.Contains(new string(name, 0, length), StringComparer.Ordinal);
    }

    private static string? ReadUnicode(IntPtr handle)
    {
        long size = (long)GlobalSize(handle).ToUInt64();
        IntPtr pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero) return null;
        try
        {
            string text = Marshal.PtrToStringUni(pointer, (int)Math.Min(size / 2, int.MaxValue / 2)) ?? "";
            int end = text.IndexOf('\0', StringComparison.Ordinal);
            return end >= 0 ? text[..end] : text;
        }
        finally { GlobalUnlock(handle); }
    }

    private static uint ReadUInt32(IntPtr handle)
    {
        IntPtr pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero) return 1;
        try { return (uint)Marshal.ReadInt32(pointer); }
        finally { GlobalUnlock(handle); }
    }

    private static ClipboardLease Open(IntPtr owner)
    {
        var deadline = DateTime.UtcNow + OpenTimeout;
        while (true)
        {
            if (OpenClipboard(owner)) return new ClipboardLease();
            if (DateTime.UtcNow >= deadline)
                throw new TextSourceException("another program has the clipboard open", new Win32Exception(Marshal.GetLastWin32Error()));
            Thread.Sleep(10);
        }
    }

    private readonly struct ClipboardLease : IDisposable
    {
        public void Dispose() => CloseClipboard();
    }

    private sealed record Snapshot(IReadOnlyList<(uint Format, byte[] Data)> Formats, bool WasSensitive, bool Truncated) : IClipboardSnapshot
    {
        public int FormatCount => Formats.Count;
    }
}
