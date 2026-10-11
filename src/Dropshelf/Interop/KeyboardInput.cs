using System.Runtime.InteropServices;

namespace Dropshelf.Interop;

/// <summary>Keyboard synthesis via SendInput.</summary>
internal static class KeyboardInput
{
    /// <summary>Sends Ctrl+V to whichever window currently has the keyboard focus.</summary>
    public static void SendCtrlV()
    {
        var inputs = new[]
        {
            Key(NativeMethods.VK_CONTROL, up: false),
            Key(NativeMethods.VK_V, up: false),
            Key(NativeMethods.VK_V, up: true),
            Key(NativeMethods.VK_CONTROL, up: true),
        };

        NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
    }

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
