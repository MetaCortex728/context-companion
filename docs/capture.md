# Capture

How context-companion takes a window, a screen region or text, and what it does not do. Everything captured goes straight to the privacy pipeline ([PRIVACY.md](../PRIVACY.md)); nothing is kept before the denylist, the metadata filter and secret redaction have run.

Capture is Windows only. The code is in `ContextCompanion.Platform.Windows` (`Capture/`, `Text/`); the platform-neutral steps (crop, downscale, the order of text sources, the terminal rule, the explicit copy's timeout and restore) are in `ContextCompanion.Core` (`Capturing/`) and are tested on Linux and Windows. Until the tray app gets its triggers, `ccomp capture` runs a capture from the command line (see the [README](../README.md#capture-from-the-command-line)).

## One frame per request

Each request takes exactly one frame. There is no recording, no timer and no background capture.

| Request | First method | Fallback | Capture mode |
| --- | --- | --- | --- |
| Window | Windows.Graphics.Capture, `CreateForWindow` | `PrintWindow` with `PW_RENDERFULLCONTENT` | `window-wgc` or `window-gdi` |
| Region on one monitor | Windows.Graphics.Capture, `CreateForMonitor`, then cropped to the region | Screen `BitBlt` with `CAPTUREBLT` | `region-wgc` or `region-gdi` |
| Region across monitors | Screen `BitBlt` with `CAPTUREBLT` | none | `region-gdi` |
| Text | see [Text](#text) | | `text-selection`, `text-clipboard` or `text-copy` |

Windows.Graphics.Capture uses a free-threaded frame pool of one frame on a Direct3D 11 device (hardware, or the WARP software device when there is no usable GPU), copies the frame into a staging texture and reads its BGRA rows. It waits at most 2 seconds for the frame. The GDI path is used when `GraphicsCaptureSession.IsSupported()` is false, when Windows.Graphics.Capture fails or delivers no frame in time, and always for a region that spans monitors (Windows.Graphics.Capture captures one monitor at a time; stitching monitors with different scales would distort the image). The capture mode in the packet says which method produced the pixels.

Windows.Graphics.Capture delivers premultiplied alpha; it is converted to straight alpha, which is what the PNG holds. GDI pixels are written as opaque.

### The capture border stays visible

While a Windows.Graphics.Capture session runs, Windows draws a coloured border around what is being captured. context-companion never changes `IsBorderRequired` and never asks for borderless capture: the tool must never be less visible than what it does. The session lasts only until the one frame arrives, so the border flashes briefly.

The GDI fallback draws no border, because Windows does not offer one for it.

### Windows the tool will not capture

- **Minimised windows** are refused with a message (`Not captured: the window is minimised…`). A minimised window has no current content; the methods would return a stale or empty frame.
- **Windows an application protects from capture** (`SetWindowDisplayAffinity`, as some password managers and DRM players do) are refused with a message. The tool does not try another method to get round the protection. Inside a region such windows appear black, because Windows blacks them out.
- **Denylisted windows** are refused before any pixels are taken; inside a region they are blacked out. See [PRIVACY.md](../PRIVACY.md#denylist-and-blackout).

For a region, the source details describe the topmost window at the centre of the region. If that window is denylisted, the whole region is refused, not just blacked out.

### What lies inside a capture

For a region, context-companion lists the visible top-level windows that intersect it, topmost first: windows that are hidden, minimised or cloaked (`DWMWA_CLOAKED`: windows on other virtual desktops, suspended Store apps) are skipped, and each window's rectangle is its visible frame (`DWMWA_EXTENDED_FRAME_BOUNDS`, which leaves out the invisible resize border and the shadow). Each comes with its process name, window class and title, used only to apply the denylist. For processes the tool may not open (for example elevated ones) the process name comes from the system's process list, so process rules still apply.

A window capture contains only that window: windows lying on top of it are not in the frame, so they are not listed.

The window title is read for matching even when titles are not recorded. With `metadata.window_title` off (the default) it never reaches the manifest, the audit log, the outbox or a message; `CaptureServiceTests` and `CaptureCliTests` check this for the capture path.

## DPI and coordinates

The app declares Per-Monitor V2 DPI awareness in its manifest; `ccomp capture` sets it at start, before it touches a window. All rectangles, including `--rect` and the `region` in a packet, are physical screen pixels in virtual-screen coordinates: on a 150 % monitor a 100 × 100 logical area is 150 × 150. A monitor to the left of or above the primary one has negative coordinates. The packet's `scale` is the effective scale of the monitor the window is on, or that holds the region (for a region across monitors, the monitor with the largest share), for example `1.5` for 150 %.

## Resizing

Before the pipeline encodes the PNG, an image whose long edge exceeds `capture.max_long_edge` (default 1568 pixels) is shrunk so that its long edge is exactly that size, keeping the aspect ratio. Images are never enlarged; `0` keeps the original size. Shrinking uses area averaging: each output pixel is the average of the exact source area it covers, with partly covered source pixels weighted by the covered fraction; colour is weighted by alpha, so transparent pixels add no colour. The `region` in the packet stays the captured screen rectangle, so a reader can map image pixels back to the screen. Denylisted windows are blacked out with their edges rounded outwards, so a shrunk image never shows a partial row or column of them.

## Text

`ccomp capture text` (and later the app) tries these sources in order and keeps the first that returns text that is not blank. The capture mode names the source that answered, and each attempt is reported:

1. **The selection** (`text-selection`): the focused element's selected text through UI Automation's `TextPattern`. Works in most editors, browsers, Office, Windows Terminal and standard edit controls, without touching the clipboard.
2. **The clipboard** (`text-clipboard`): the clipboard's current text, **only** when you ask for it (`--clipboard`). Content that the application which put it there marked as sensitive (`ExcludeClipboardContentFromMonitorProcessing`, or `CanIncludeInClipboardHistory` = 0, as password managers do) is not read.
3. **An explicit copy** (`text-copy`), on by default (`capture.explicit_copy`): the clipboard is saved, Ctrl+C is sent to the focused window with `SendInput`, the tool waits until the clipboard sequence number changes (at most `capture.copy_timeout_ms`, default 500 ms), reads the text and restores the saved clipboard. The restore always runs once the clipboard was saved: after a timeout, after a failure, and when the copied content turns out to be marked sensitive (it is then discarded). Ctrl+C is not sent while Shift, Alt or a Windows key is held down.

A denylisted foreground window is refused before any source is tried, so no keystroke reaches it.

### Terminals: no Ctrl+C

In a console or terminal, Ctrl+C interrupts the running program. context-companion never sends it to a window of these classes; the explicit copy is skipped and the result says why (`not sent to a console or terminal window, where Ctrl+C interrupts the running program`). The selection through UI Automation still works there (Windows Terminal supports it).

| Window class | Application |
| --- | --- |
| `ConsoleWindowClass` | Windows console host (cmd, PowerShell, other console programs) |
| `CASCADIA_HOSTING_WINDOW_CLASS` | Windows Terminal |
| `mintty` | mintty (Git Bash, Cygwin, MSYS2) |
| `PuTTY` | PuTTY |
| `VirtualConsoleClass` | ConEmu and Cmder |
| `org.wezfurlong.wezterm` | WezTerm |
| `Alacritty` | Alacritty |

The list is data (`src/ContextCompanion.Core/Capturing/Defaults/no-copy-window-classes.json`). You can add classes with `capture.extra_terminal_window_classes`; the built-in ones cannot be removed. The copy is also skipped when the focused window's class cannot be read.

### What the explicit copy does to the clipboard

The copy temporarily replaces your clipboard, so the tool saves and restores it. It restores these formats faithfully, as exact copies of their data:

- text: `CF_UNICODETEXT`, `CF_TEXT`, `CF_OEMTEXT`, `CF_LOCALE`;
- images: `CF_DIB`, `CF_DIBV5`;
- files copied in Explorer: `CF_HDROP`;
- registered formats, for example `HTML Format`, `Rich Text Format`, `PNG` and applications' own formats.

**Anything else on the clipboard is lost** by the restore: formats held as GDI objects (`CF_BITMAP` when no DIB form is offered, metafiles, palettes), private and owner-drawn formats, OLE's own data-object formats (`DataObject`, `Ole Private Data`, embedding and link formats), content an application offers only on demand that it fails to render, and anything beyond 64 MiB in total. After the restore you can paste the saved formats, but the application that copied them no longer owns the clipboard. If an application puts the copy on the clipboard after the timeout, that late copy replaces the restored content.

### Nothing the tool writes reaches clipboard history

Whenever context-companion writes to the clipboard (today only to restore it) it also sets:

- `ExcludeClipboardContentFromMonitorProcessing` (clipboard monitors and managers should ignore the content),
- `CanIncludeInClipboardHistory` = 0 (not added to Windows clipboard history, Win+V),
- `CanUploadToCloudClipboard` = 0 (not synchronised to other devices).

A restored clipboard therefore stays out of clipboard history even if the original content was in it (it is already there from the original copy). When the restored content was not marked sensitive before, the tool also adds a `ContextCompanion.RestoredClipboard` marker, so that a later `--clipboard` read can tell its own restore apart from content a password manager marked as sensitive. Applications that ignore these formats are outside the tool's control.

## Settings

The `capture` section of the settings file (the same file as the privacy settings, versioned by its `schema_version`; a property left out keeps its default; unknown properties are refused):

```json
"capture": {
  "max_long_edge": 1568,
  "include_cursor": false,
  "explicit_copy": true,
  "copy_timeout_ms": 500,
  "extra_terminal_window_classes": []
}
```

| Setting | Default | What it does |
| --- | --- | --- |
| `max_long_edge` | 1568 | Longest image edge in pixels after resizing; 0 keeps the original size; otherwise 16 to 16384. |
| `include_cursor` | off | Includes the mouse pointer in window and region captures. |
| `explicit_copy` | on | Allows the explicit copy (Ctrl+C with clipboard restore) as the last text source. |
| `copy_timeout_ms` | 500 | How long the explicit copy waits for the clipboard to change, 50 to 5000 ms. |
| `extra_terminal_window_classes` | none | Window classes added to the built-in list where Ctrl+C is never sent. |

## Limits

- Some applications cannot be captured, or come out black: protected windows, some hardware-accelerated or DRM video, some games in exclusive full screen. The tool reports a refusal or delivers what Windows gives it; it does not work around protection.
- `PrintWindow` asks the application to draw itself; a few applications draw incompletely or not at all that way.
- The GDI region path copies what is on screen at that moment, including windows on top; the window list for the denylist is taken just before, so a window that appears in between is not blacked out.
- UI Automation depends on what each application exposes. Some expose no selection; then the explicit copy runs.
- The explicit copy sends a real keystroke to the focused application. If that application uses Ctrl+C for something other than copy, that is what happens. Only terminals are recognised and skipped.
- Windows of elevated programs may refuse `PrintWindow`, UI Automation and synthesised keystrokes from a normal process (User Interface Privilege Isolation). The attempt then fails or times out, and the result says so; Windows does not always report why.
- Windows 10 version 2004 (build 19041) or later is required.
