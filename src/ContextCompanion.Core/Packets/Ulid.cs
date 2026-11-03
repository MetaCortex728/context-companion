using System.Security.Cryptography;

namespace ContextCompanion.Core.Packets;

/// <summary>
/// ULID helpers: 26 characters of Crockford base32, a 48-bit millisecond timestamp followed by
/// 80 bits of randomness. Canonical form is upper case; that is the only form packets accept.
/// </summary>
public static class Ulid
{
    public const int Length = 26;
    internal const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    public const long MaxTimestamp = (1L << 48) - 1;

    /// <summary>True for a canonical (upper-case, 26-character, non-overflowing) ULID.</summary>
    public static bool IsValid(string? value)
    {
        if (value is null || value.Length != Length || value[0] > '7') return false;
        foreach (char c in value)
            if (Alphabet.IndexOf(c, StringComparison.Ordinal) < 0) return false;
        return true;
    }

    /// <summary>The Unix time in milliseconds encoded in a canonical ULID.</summary>
    public static long GetTimestamp(string value)
    {
        if (!IsValid(value)) throw new FormatException("Not a canonical ULID.");
        long time = 0;
        for (int i = 0; i < 10; i++) time = (time << 5) | (long)Alphabet.IndexOf(value[i], StringComparison.Ordinal);
        return time;
    }

    /// <summary>Encodes a timestamp and 10 random bytes (big-endian) as a canonical ULID.</summary>
    public static string Encode(long timestamp, ReadOnlySpan<byte> random)
    {
        if (timestamp < 0 || timestamp > MaxTimestamp) throw new ArgumentOutOfRangeException(nameof(timestamp));
        if (random.Length != 10) throw new ArgumentException("A ULID needs exactly 10 random bytes.", nameof(random));
        Span<char> chars = stackalloc char[Length];
        long time = timestamp;
        for (int i = 9; i >= 0; i--) { chars[i] = Alphabet[(int)(time & 31)]; time >>= 5; }
        // 80 random bits = 16 characters of 5 bits.
        UInt128 bits = 0;
        foreach (byte b in random) bits = (bits << 8) | b;
        for (int i = Length - 1; i >= 10; i--) { chars[i] = Alphabet[(int)(bits & 31)]; bits >>= 5; }
        return new string(chars);
    }
}

/// <summary>
/// Monotonic ULID generator. Within one millisecond (or if the clock steps backwards) the random
/// part of the previous identifier is incremented, so identifiers from one generator always sort
/// in creation order. Thread-safe.
/// </summary>
public sealed class UlidGenerator
{
    private static readonly UInt128 RandomMax = (UInt128.One << 80) - 1;
    private readonly TimeProvider time;
    private readonly Action<Span<byte>> fillRandom;
    private readonly Lock gate = new();
    private long lastTimestamp = -1;
    private UInt128 lastRandom;

    public UlidGenerator(TimeProvider? time = null, Action<Span<byte>>? fillRandom = null)
    {
        this.time = time ?? TimeProvider.System;
        this.fillRandom = fillRandom ?? RandomNumberGenerator.Fill;
    }

    public static UlidGenerator Shared { get; } = new();

    public string NewId()
    {
        long now = time.GetUtcNow().ToUnixTimeMilliseconds();
        if (now < 0 || now > Ulid.MaxTimestamp) throw new InvalidOperationException("The clock is outside the ULID range.");
        lock (gate)
        {
            if (now <= lastTimestamp)
            {
                if (lastRandom == RandomMax)
                    throw new InvalidOperationException("ULID random component overflowed within one millisecond.");
                lastRandom++;
            }
            else
            {
                Span<byte> fresh = stackalloc byte[10];
                fillRandom(fresh);
                UInt128 value = 0;
                foreach (byte b in fresh) value = (value << 8) | b;
                lastTimestamp = now;
                lastRandom = value;
            }
            Span<byte> bytes = stackalloc byte[10];
            UInt128 r = lastRandom;
            for (int i = 9; i >= 0; i--) { bytes[i] = (byte)(r & 0xff); r >>= 8; }
            return Ulid.Encode(lastTimestamp, bytes);
        }
    }
}
