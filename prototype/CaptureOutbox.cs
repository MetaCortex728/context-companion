using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace DomoCompanion;

public sealed record CaptureAttempt(string Id, string ThreadId, string Mode, DateTimeOffset Created,
    string Status, int Bytes, string Note, string? ContentHash = null, string Caption = "",
    DateTimeOffset? SendStarted = null, DateTimeOffset? SendFinished = null, Dictionary<string, double>? SendTimings = null,
    DateTimeOffset? ReceivedAt = null, string? SourceThreadId = null);

public sealed class CaptureOutbox
{
    public const int PendingLimit = 4, ArchiveLimit = 128;
    public const long ArchiveBytes = 256L * 1024 * 1024;
    private readonly object gate = new();
    private readonly List<CaptureAttempt> attempts;
    public string Root { get; }
    private string Manifest => Path.Combine(Root, "attempts.json");
    public CaptureOutbox(string root)
    {
        Root = Path.GetFullPath(root);
        if (!Root.StartsWith("C:\\", StringComparison.OrdinalIgnoreCase)) throw new IOException("Capture storage must stay on C:.");
        Directory.CreateDirectory(Root);
        if (File.Exists(Manifest))
        {
            if (new FileInfo(Manifest).Length > 512 * 1024) throw new IOException("Outbox manifest exceeds its limit.");
            attempts = JsonSerializer.Deserialize<List<CaptureAttempt>>(File.ReadAllText(Manifest)) ?? throw new IOException("Outbox manifest is invalid.");
            if (attempts.Count > ArchiveLimit || attempts.Any(a => a is null) ||
                attempts.Select(a => a.Id).Distinct().Count() != attempts.Count ||
                attempts.Any(a => !Guid.TryParseExact(a.Id, "D", out _) || !Guid.TryParseExact(a.ThreadId, "D", out _) ||
                    a.Bytes < 0 || a.Bytes > Capture.MaxPngBytes || a.Mode is not ("window" or "cursor" or "test") ||
                    (a.ContentHash is not null && (a.ContentHash.Length != 64 || !a.ContentHash.All(Uri.IsHexDigit))) ||
                    a.Caption is null || a.Caption.Length > 2000 ||
                    (a.SourceThreadId is not null && !Guid.TryParseExact(a.SourceThreadId, "D", out _)) ||
                    a.Status is not ("queued" or "sending" or "accepted" or "received" or "uncertain" or "failed")))
                throw new IOException("Outbox manifest is invalid; existing images were preserved.");
            for (int i = 0; i < attempts.Count; i++)
                if (attempts[i].Status == "sending") attempts[i] = attempts[i] with { Status = "uncertain", Note = "Interrupted during delivery; not retried." };
            Save(attempts);
        }
        else attempts = [];
    }
    public CaptureAttempt[] Snapshot() { lock (gate) return attempts.ToArray(); }
    public string ImagePath(string id)
    {
        if (!Guid.TryParseExact(id, "D", out _)) throw new ArgumentException("Invalid capture ID.");
        return Path.Combine(Root, id + ".png");
    }
    public static bool Pending(CaptureAttempt attempt) => attempt.Status is "queued" or "sending" or "uncertain";
    public void CheckCapacity()
    {
        lock (gate)
        {
            if (attempts.Count >= ArchiveLimit || attempts.Count(Pending) >= PendingLimit)
                throw new IOException("Capture queue or archive is full; no new image captured.");
            long diskBytes = Directory.EnumerateFiles(Root, "*.png", SearchOption.TopDirectoryOnly).Sum(p => new FileInfo(p).Length);
            if (diskBytes + Capture.MaxPngBytes > ArchiveBytes) throw new IOException("Capture archive is full; no new image captured.");
        }
    }
    public CaptureAttempt? Add(string threadId, string mode, byte[] png, string caption = "")
    {
        if (!Guid.TryParseExact(threadId, "D", out _) || mode is not ("window" or "cursor" or "test")) throw new ArgumentException("Invalid capture destination or mode.");
        if (png.Length < 24 || png.Length > Capture.MaxPngBytes || !png.AsSpan(0, 8).SequenceEqual(new byte[] { 137,80,78,71,13,10,26,10 }))
            throw new IOException("Invalid capture image.");
        if (caption.Length > 2000) throw new ArgumentException("Capture text exceeds its limit.");
        string hash = Convert.ToHexString(SHA256.HashData(png));
        lock (gate)
        {
            var previous = attempts.LastOrDefault(a => a.ThreadId == threadId);
            if (previous is not null && previous.Status != "failed" && previous.Caption == caption && previous.Bytes == png.Length)
            {
                string? previousHash = previous.ContentHash;
                string previousPath = ImagePath(previous.Id);
                if (previousHash is null && File.Exists(previousPath) && new FileInfo(previousPath).Length <= Capture.MaxPngBytes)
                    previousHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(previousPath)));
                if (hash == previousHash) return null;
            }
            CheckCapacity();
            string? source = Environment.GetEnvironmentVariable("CODEX_THREAD_ID");
            if (!Guid.TryParseExact(source, "D", out _)) source = null;
            var attempt = new CaptureAttempt(Guid.NewGuid().ToString(), threadId, mode, DateTimeOffset.Now, "queued", png.Length,
                "Waiting for app handoff.", hash, caption, SourceThreadId: source);
            string path = ImagePath(attempt.Id);
            var next = attempts.Append(attempt).ToList();
            try
            {
                File.WriteAllBytes(path + ".tmp", png); File.Move(path + ".tmp", path);
                Save(next);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Only this uncommitted capture is ours to remove; preserve every existing attempt.
                foreach (string incomplete in new[] { path, path + ".tmp" })
                    try { File.Delete(incomplete); }
                    catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
                throw;
            }
            attempts.Add(attempt); return attempt;
        }
    }
    public void SetStatus(string id, string status, string note, Dictionary<string, double>? timings = null)
    {
        if (status is not ("queued" or "sending" or "accepted" or "uncertain" or "failed")) throw new ArgumentException("Invalid status.");
        lock (gate)
        {
            int index = attempts.FindIndex(a => a.Id == id);
            if (index < 0) throw new ArgumentException("Unknown capture.");
            var next = attempts.ToList(); next[index] = next[index] with
            {
                Status = next[index].Status == "received" ? "received" : status,
                Note = next[index].Status == "received" ? next[index].Note : note,
                SendStarted = status == "sending" ? DateTimeOffset.UtcNow : next[index].SendStarted,
                SendFinished = status == "sending" ? null : DateTimeOffset.UtcNow,
                SendTimings = timings
            };
            Save(next); attempts[index] = next[index];
        }
    }
    public bool ConfirmReceipt(string threadId, string id, DateTimeOffset receivedAt)
    {
        lock (gate)
        {
            int index = attempts.FindIndex(a => a.Id == id && a.ThreadId == threadId);
            if (index < 0 || attempts[index].Status is not ("sending" or "accepted" or "uncertain") ||
                receivedAt < attempts[index].Created || receivedAt > DateTimeOffset.UtcNow.AddMinutes(1)) return false;
            var next = attempts.ToList();
            next[index] = next[index] with { Status = "received", ReceivedAt = receivedAt,
                Note = "Handoff observed in destination chat; not proof of image viewing or an answer." };
            Save(next); attempts[index] = next[index]; return true;
        }
    }
    private void Save(List<CaptureAttempt> items)
    {
        string json = JsonSerializer.Serialize(items);
        for (int retry = 0; ; retry++)
        {
            try
            {
                File.WriteAllText(Manifest + ".tmp", json);
                File.Move(Manifest + ".tmp", Manifest, overwrite: true);
                return;
            }
            catch (Exception ex) when (retry < 3 && (ex is IOException or UnauthorizedAccessException) &&
                (ex.HResult & 0xffff) is 5 or 32 or 33 &&
                !Directory.Exists(Manifest) && !Directory.Exists(Manifest + ".tmp"))
            {
                // Windows can briefly deny replacement while another reader holds the file.
                // Retry only this local atomic write; never a network send.
                Thread.Sleep(20 << retry);
            }
        }
    }
}
