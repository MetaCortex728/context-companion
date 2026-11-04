namespace ContextCompanion.Core.Packets;

/// <summary>
/// Encrypts and decrypts packet payload files at rest. Core defines only this interface; the Windows DPAPI
/// implementation lives in ContextCompanion.Platform.Windows.
/// </summary>
public interface IPayloadProtector
{
    /// <summary>The value written to an item's <c>stored.protection</c>, for example <c>dpapi-current-user</c>: a lower-case token.</summary>
    string Protection { get; }

    /// <summary>Extension added to the stored file name, without the dot, for example <c>dpapi</c>.</summary>
    string FileExtension { get; }

    byte[] Protect(byte[] plaintext);

    /// <summary>Decrypts. Throws <see cref="System.Security.Cryptography.CryptographicException"/> if the data cannot be decrypted.</summary>
    byte[] Unprotect(byte[] stored);
}
