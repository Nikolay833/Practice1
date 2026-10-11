using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Dropshelf.Services;

namespace Dropshelf.Interop;

/// <summary>Keyboard synthesis via SendInput.</summary>
internal static class KeyboardInput
{
    private const int ModifierWaitMs = 1000;
    private const int ModifierPollMs = 15;

    /// <summary>
    /// Sends Ctrl+V to whichever window currently has the keyboard focus. First waits (up to about a second, without
    /// blocking the UI thread) until Shift, Alt and Win are physically released, otherwise the target would see
    /// Ctrl+Shift+V and friends. Returns false when the keys stayed down or SendInput failed.
    /// </summary>
    public static async Task<bool> SendCtrlVAsync()
    {
        for (int waited = 0; AnyModifierDown(); waited += ModifierPollMs)
        {
            if (waited >= ModifierWaitMs)
            {
                Log.Error("Paste skipped: Shift/Alt/Win stayed pressed");
                return false;
            }

            await Task.Delay(ModifierPollMs);
        }

        var inputs = new[]
        {
            Key(NativeMethods.VK_CONTROL, up: false),
            Key(NativeMethods.VK_V, up: false),
            Key(NativeMethods.VK_V, up: true),
            Key(NativeMethods.VK_CONTROL, up: true),
        };

        uint sent = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
        if (sent != inputs.Length)
        {
            Log.Error($"SendInput sent {sent} of {inputs.Length} events (error {Marshal.GetLastWin32Error()})");
            return false;
        }

        return true;
    }

    private static bool AnyModifierDown() =>
        IsDown(NativeMethods.VK_SHIFT) || IsDown(NativeMethods.VK_MENU) ||
        IsDown(NativeMethods.VK_LWIN) || IsDown(NativeMethods.VK_RWIN);

    private static bool IsDown(int vk) => (NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0;

    private static NativeMethods.INPUT Key(ushort vk, bool up) => new()
    {
        type = NativeMethods.INPUT_KEYBOARD,
        U = new NativeMethods.INPUTUNION
        {
            ki = new NativeMethods.KEYBDINPUT
            {
                wVk = vk,
                dwFlags = up ? NativeMethods.KEYEVENTF_KEYUP : 0,
            },
        },
    };
}
