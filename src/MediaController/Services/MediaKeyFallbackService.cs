using System.Runtime.InteropServices;
using MediaController.Core;
using MediaController.Native;

namespace MediaController.Services;

/// <summary>
/// Last resort only: synthesizes a real system media key with SendInput so whatever
/// Windows routes media keys to reacts. Never used while a GSMTC session accepts the command.
/// </summary>
public sealed class MediaKeyFallbackService
{
    public void Send(MediaAction action)
    {
        var vk = action switch
        {
            MediaAction.Next => VirtualKey.VK_MEDIA_NEXT_TRACK,
            MediaAction.Previous => VirtualKey.VK_MEDIA_PREV_TRACK,
            _ => VirtualKey.VK_MEDIA_PLAY_PAUSE
        };

        try
        {
            var inputs = new NativeMethods.INPUT[2];

            inputs[0].type = NativeMethods.INPUT_KEYBOARD;
            inputs[0].u.ki = new NativeMethods.KEYBDINPUT { wVk = vk, dwFlags = 0 };

            inputs[1].type = NativeMethods.INPUT_KEYBOARD;
            inputs[1].u.ki = new NativeMethods.KEYBDINPUT { wVk = vk, dwFlags = NativeMethods.KEYEVENTF_KEYUP };

            var sent = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
            if (sent != inputs.Length)
            {
                // Typically UIPI: an elevated foreground window blocks synthetic input.
                Logger.Warn($"Media key fallback for {action}: SendInput delivered {sent}/{inputs.Length} events (Win32 error {Marshal.GetLastWin32Error()}).");
            }
            else
            {
                Logger.Info($"Media key fallback used for {action}.");
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Media key fallback for {action} failed.", ex);
        }
    }
}
