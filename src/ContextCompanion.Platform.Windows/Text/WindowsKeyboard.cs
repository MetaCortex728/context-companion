using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ContextCompanion.Core.Capturing;
using ContextCompanion.Platform.Windows.Interop;
using static ContextCompanion.Platform.Windows.Interop.NativeMethods;

namespace ContextCompanion.Platform.Windows.Text;

/// <summary>
/// Sends Ctrl+C to the focused window with <c>SendInput</c>. Refuses while Shift, Alt or a Windows key is held, because
/// the keystroke would then become a different shortcut. Callers never send it to terminals (see
/// <see cref="ExplicitCopyTextSource"/>).
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class WindowsKeyboard : IKeyboardInput
{
    public void SendCopy()
    {
        foreach (int key in new[] { (int)VK_SHIFT, VK_MENU, VK_LWIN, VK_RWIN })
            if ((GetAsyncKeyState(key) & 0x8000) != 0)
                throw new TextSourceException("Ctrl+C was not sent because Shift, Alt or a Windows key is held down");

        INPUT[] inputs = [Key(VK_CONTROL, up: false), Key(VK_C, up: false), Key(VK_C, up: true), Key(VK_CONTROL, up: true)];
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
            throw new TextSourceException("Ctrl+C could not be sent (the input was blocked, for example by a window of higher integrity)");
    }

    private static INPUT Key(ushort key, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion { ki = new KEYBDINPUT { wVk = key, dwFlags = up ? KEYEVENTF_KEYUP : 0 } },
    };
}
