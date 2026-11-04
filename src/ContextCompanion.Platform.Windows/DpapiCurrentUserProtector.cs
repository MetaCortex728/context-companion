using System.Runtime.Versioning;
using System.Security.Cryptography;
using ContextCompanion.Core.Packets;

namespace ContextCompanion.Platform.Windows;

/// <summary>
/// Encrypts payload files with Windows DPAPI for the current user (<see cref="DataProtectionScope.CurrentUser"/>,
/// no additional entropy). Only the same Windows user account can decrypt them, on the same machine (or, with a
/// roaming profile, wherever that profile's keys are). It protects against other accounts and an offline disk, not
/// against software running as the same user.
/// </summary>
/// <remarks>
/// No additional entropy is used so that any consumer running as the user can decrypt with the standard API, for
/// example <c>ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser)</c>; see PRIVACY.md.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class DpapiCurrentUserProtector : IPayloadProtector
{
    public string Protection => PacketFormat.DpapiCurrentUser;
    public string FileExtension => PacketFormat.DpapiExtension;

    public byte[] Protect(byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        return ProtectedData.Protect(plaintext, optionalEntropy: null, DataProtectionScope.CurrentUser);
    }

    public byte[] Unprotect(byte[] stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        return ProtectedData.Unprotect(stored, optionalEntropy: null, DataProtectionScope.CurrentUser);
    }
}
