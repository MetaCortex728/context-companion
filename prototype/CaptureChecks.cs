using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text.Json;

namespace DomoCompanion;

public static class CaptureChecks
{
    public static int Run()
    {
        int count = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception("FAIL: " + name);
            count++; Console.WriteLine("PASS: " + name);
        }
        try
        {
            var screen = new PixelRect(0, 0, 1920, 1080);
            Check(Capture.Crop(screen, 960, 540) == new PixelRect(576, 324, 768, 432), "40 percent of each dimension; cursor centered");
            Check(Capture.Crop(screen, 0, 0) == new PixelRect(0, 0, 768, 432), "top left crop clamps within monitor");
            Check(Capture.Crop(screen, 1919, 1079) == new PixelRect(1152, 648, 768, 432), "bottom right crop clamps within monitor");
            var left = Capture.Crop(new(-1920, 0, 1920, 1080), -1910, 540);
            Check(left == new PixelRect(-1920, 324, 768, 432), "negative monitor coordinates preserved");
            var portrait = Capture.Crop(new(0, 0, 1080, 1920), 540, 960);
            Check(portrait.Width == 432 && portrait.Height == 768, "portrait aspect ratio preserved");
            var fourK = Capture.Crop(new(0, 0, 3840, 2160), 1920, 1080);
            Check(fourK.Width == 1536 && fourK.Height == 864, "4K crop dimensions correct");
            Check(Capture.Fits(new(0, 0, 3840, 2160)), "4K window fits raw budget");
            Check(!Capture.Fits(new(0, 0, 7680, 4320)), "oversized window rejected, not full screen fallback");
            Check(!Capture.Fits(new(0, 0, int.MaxValue, int.MaxValue)), "pixel multiplication cannot overflow");
            Check(!Capture.Fits(new(0, 0, 0, 10)), "empty image rejected");
            uint negative = unchecked((uint)(-120 << 16)), positive = 120u << 16;
            Check(CaptureMouse.Mode(0x20e, negative, 0) == "window", "scroll left selects foreground window");
            Check(CaptureMouse.Mode(0x20e, positive, 0) == "cursor", "scroll right selects cursor crop");
            Check(CaptureMouse.Mode(0x20a, negative, 0) is null, "ordinary vertical scrolling unaffected");
            Check(CaptureMouse.Mode(0x20e, negative, 1) is null, "injected input ignored");
            Check(CaptureMouse.Mode(0x20e, positive, 2) is null, "lower integrity injected input ignored");
            Check(CaptureMouse.Mode(0x20e, 0, 0) is null, "zero delta ignored");
            string root = Path.Combine(Program.StateDirectory, "capture-tests", Guid.NewGuid().ToString());
            var box = new CaptureOutbox(root);
            using var bitmap = new Bitmap(32, 32);
            using (var graphics = Graphics.FromImage(bitmap)) { graphics.Clear(Color.Navy); graphics.FillRectangle(Brushes.White, 4, 4, 12, 12); }
            using var stream = new MemoryStream(); bitmap.Save(stream, ImageFormat.Png);
            byte[] png = stream.ToArray();
            string original = Program.InitialThread, other = Guid.NewGuid().ToString();
            var first = box.Add(original, "window", png)!;
            Check(File.Exists(box.ImagePath(first.Id)), "image is durable before queue entry");
            Check(box.Snapshot()[0].ThreadId == original, "destination recorded per capture");
            box.SetStatus(first.Id, "sending", "test");
            box = new CaptureOutbox(root);
            Check(box.Snapshot()[0].Status == "uncertain", "restart changes in-flight delivery to uncertain");
            Check(!box.Snapshot().Any(a => a.Status == "queued"), "uncertain outcome cannot be automatically retried");
            for (int i = 0; i < 3; i++) box.Add(other, "cursor", png, i.ToString());
            bool full = false;
            try { box.Add(other, "cursor", png, "new"); } catch (IOException) { full = true; }
            Check(full && box.Snapshot().Length == 4, "four pending limit preserves existing captures");
            Check(box.Snapshot()[0].ThreadId == original, "new destination cannot retarget old capture");
            box.SetStatus(first.Id, "accepted", "test");
            Check(File.Exists(box.ImagePath(first.Id)), "accepted file retained for delayed model reading");
            var fifth = box.Add(other, "window", png)!;
            Check(box.Snapshot().Count(CaptureOutbox.Pending) == 4, "accepted receipt releases pending slot");
            bool bad = false;
            try { box.ImagePath("../escape"); } catch (ArgumentException) { bad = true; }
            Check(bad, "image paths restricted to stable capture IDs");
            box.SetStatus(fifth.Id, "failed", "test");
            bad = false;
            try { box.Add(original, "window", new byte[32]); } catch (IOException) { bad = true; }
            Check(bad, "non-PNG data rejected");
            Check(CaptureOutbox.Pending(first with { Status = "uncertain" }), "uncertain items count against queue capacity");
            File.WriteAllText(Path.Combine(root, "attempts.json"), "{bad");
            bad = false;
            try { _ = new CaptureOutbox(root); } catch (JsonException) { bad = true; }
            Check(bad && File.Exists(box.ImagePath(first.Id)), "corrupt manifest fails closed without erasing images");
            var dedup = new CaptureOutbox(Path.Combine(root, "dedup"));
            var sent = dedup.Add(original, "window", png)!;
            Check(dedup.Add(original, "window", png) is null, "identical queued image discarded before second send");
            Check(dedup.Add(original, "cursor", png) is null, "capture mode is not a reason to resend identical content");
            Check(dedup.Snapshot().Length == 1 && Directory.GetFiles(dedup.Root, "*.png").Length == 1, "duplicate adds no history entry or image file");
            dedup.SetStatus(sent.Id, "accepted", "test");
            Check(dedup.Add(original, "window", png) is null, "accepted consecutive duplicate discarded");
            dedup = new CaptureOutbox(dedup.Root);
            Check(dedup.Add(original, "window", png) is null, "deduplication survives restart");
            Check(dedup.Add(other, "window", png) is not null, "same image can go to another destination");
            Check(dedup.Add(original, "window", png) is null, "other destination does not reset original duplicate check");
            var captioned = dedup.Add(original, "window", png, "different text")!;
            Check(captioned is not null, "same image with changed text is retained");
            Check(dedup.Add(original, "window", png, "different text") is null, "identical image plus text discarded");
            dedup.SetStatus(captioned!.Id, "uncertain", "test");
            Check(dedup.Add(original, "window", png, "different text") is null, "uncertain duplicate is not resent");
            dedup.SetStatus(captioned.Id, "failed", "test");
            Check(dedup.Add(original, "window", png, "different text") is not null, "definitely failed capture can be explicitly attempted again");
            Check(dedup.Add(original, "window", png) is not null, "nonconsecutive content is not globally suppressed");
            string corruptRoot = Path.Combine(root, "null-record");
            Directory.CreateDirectory(corruptRoot);
            File.WriteAllText(Path.Combine(corruptRoot, "attempts.json"), "[null]");
            bad = false;
            try { _ = new CaptureOutbox(corruptRoot); } catch (IOException) { bad = true; }
            Check(bad, "null manifest entry fails closed as a recoverable outbox error");

            var unwritable = new CaptureOutbox(Path.Combine(root, "write-failure"));
            var retained = unwritable.Add(original, "window", png)!;
            string manifest = Path.Combine(unwritable.Root, "attempts.json");
            string beforeFailure = File.ReadAllText(manifest);
            Directory.CreateDirectory(manifest + ".tmp");
            bad = false;
            try { unwritable.Add(original, "window", png, "new capture"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { bad = true; }
            Check(bad && unwritable.Snapshot().Length == 1, "failed manifest write does not add an in-memory capture");
            Check(Directory.GetFiles(unwritable.Root, "*.png").Length == 1 && File.Exists(unwritable.ImagePath(retained.Id)),
                "failed enqueue removes only its orphan image and preserves previous capture");
            Check(File.ReadAllText(manifest) == beforeFailure, "failed enqueue preserves committed manifest");
            bad = false;
            try { unwritable.SetStatus(retained.Id, "accepted", "test"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { bad = true; }
            Check(bad && unwritable.Snapshot()[0].Status == "queued", "failed status persistence cannot claim acceptance in memory");
            Directory.Delete(manifest + ".tmp");
            Check(new CaptureOutbox(unwritable.Root).Snapshot()[0].Id == retained.Id, "outbox remains readable after write failure resolves");
            var transientLock = new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.Read);
            var releaseLock = Task.Run(async () => { await Task.Delay(30); transientLock.Dispose(); });
            try { unwritable.SetStatus(retained.Id, "accepted", "local write retry test"); }
            finally { releaseLock.GetAwaiter().GetResult(); transientLock.Dispose(); }
            Check(unwritable.Snapshot()[0].Status == "accepted", "brief external file lock recovers within bounded local write retry");
            using (var persistentLock = new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                bad = false; var elapsed = System.Diagnostics.Stopwatch.StartNew();
                try { unwritable.SetStatus(retained.Id, "failed", "must not commit"); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { bad = true; }
                Check(bad && elapsed.Elapsed < TimeSpan.FromSeconds(2) && unwritable.Snapshot()[0].Status == "accepted",
                    "persistent lock fails within a bound without changing committed status");
            }
            Console.WriteLine($"{count} capture/outbox checks passed. Actual mouse, target app capture and sharing are not covered.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
