namespace ContextCompanion.Core.Packets;

/// <summary>
/// Name rules for packet item files, consumers and destinations. Item file names are deliberately
/// narrow so that the same name is safe on Windows, macOS and Linux and can never form a path.
/// </summary>
public static class PacketNames
{
    private static readonly string[] ReservedDeviceNames =
    [
        "CON", "PRN", "AUX", "NUL", "CLOCK$", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    /// <summary>
    /// Returns null if <paramref name="name"/> is an acceptable item file name, otherwise the reason.
    /// Allowed: 1-100 characters from A-Z, a-z, 0-9, '.', '_' and '-', starting with a letter or digit,
    /// not ending in '.', no "..", not a reserved Windows device name (with or without extension),
    /// and not one of the packet's own file names.
    /// </summary>
    public static string? CheckItemFileName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return "file name is empty";
        if (name.Length > PacketFormat.MaxFileNameLength) return $"file name is longer than {PacketFormat.MaxFileNameLength} characters";
        foreach (char c in name)
        {
            if (c is '/' or '\\') return "file name contains a path separator";
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')) return "file name contains a character outside A-Z, a-z, 0-9, '.', '_' and '-'";
        }
        if (name.Contains("..", StringComparison.Ordinal)) return "file name contains '..'";
        if (!char.IsAsciiLetterOrDigit(name[0])) return "file name must start with a letter or digit";
        if (name[^1] == '.') return "file name must not end with '.'";
        string stem = name.Split('.')[0];
        if (ReservedDeviceNames.Contains(stem, StringComparer.OrdinalIgnoreCase)) return "file name is a reserved Windows device name";
        if (name.Equals(PacketFormat.ManifestFileName, StringComparison.OrdinalIgnoreCase) ||
            name.Equals(PacketFormat.AckFileName, StringComparison.OrdinalIgnoreCase))
            return "file name is reserved for the packet itself";
        return null;
    }

    public static bool IsValidItemFileName(string? name) => CheckItemFileName(name) is null;

    /// <summary>Consumer and destination names: 1-64 characters, A-Z a-z 0-9 . _ -, starting with a letter or digit.</summary>
    public static bool IsValidAgentName(string? name) =>
        !string.IsNullOrEmpty(name) && name.Length <= PacketFormat.MaxConsumerLength &&
        char.IsAsciiLetterOrDigit(name[0]) &&
        name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    /// <summary>Short identifiers such as capture modes and redaction kinds: lower case, digits and '-', 1-32 characters.</summary>
    public static bool IsValidToken(string? value) =>
        !string.IsNullOrEmpty(value) && value.Length <= 32 && char.IsAsciiLetterLower(value[0]) &&
        value.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');

    /// <summary>A media type such as <c>image/png</c>: lower-case type and subtype, no parameters.</summary>
    public static bool IsValidMediaType(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 127) return false;
        int slash = value.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0 || slash != value.LastIndexOf('/') || slash == value.Length - 1) return false;
        return IsMediaToken(value.AsSpan(0, slash)) && IsMediaToken(value.AsSpan(slash + 1));

        static bool IsMediaToken(ReadOnlySpan<char> token)
        {
            if (token.Length is 0 or > 63 || !(char.IsAsciiLetterLower(token[0]) || char.IsAsciiDigit(token[0]))) return false;
            foreach (char c in token)
                if (!(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '!' or '#' or '$' or '&' or '^' or '_' or '.' or '+' or '-'))
                    return false;
            return true;
        }
    }

    /// <summary>The media type family each item kind must use.</summary>
    public static bool MediaTypeMatchesKind(PacketItemKind kind, string mediaType) => kind switch
    {
        PacketItemKind.Image => mediaType.StartsWith("image/", StringComparison.Ordinal),
        PacketItemKind.Audio => mediaType.StartsWith("audio/", StringComparison.Ordinal),
        PacketItemKind.Text or PacketItemKind.Note => mediaType.StartsWith("text/", StringComparison.Ordinal),
        _ => false,
    };

    /// <summary>True if <paramref name="value"/> holds no control characters other than tab, CR and LF (when allowed).</summary>
    internal static bool HasNoControlCharacters(string value, bool allowLineBreaks) =>
        value.All(c => !char.IsControl(c) || (allowLineBreaks && c is '\n' or '\r' or '\t'));
}
