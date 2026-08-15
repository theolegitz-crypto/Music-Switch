using System.Runtime.InteropServices;
using MediaController.Core;

namespace MediaController.Native;

/// <summary>
/// Optional Windows 11 window materials. Every method reports success instead of throwing:
/// the app must look right on Windows 10 too, where none of these attributes exist.
/// The build check is only a guard - the HRESULT is what actually decides.
/// </summary>
public static class DwmHelper
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    private const int DWMSBT_MAINWINDOW = 2;     // Mica
    private const int DWMSBT_TRANSIENTWINDOW = 3; // Acrylic

    private const int DWMWCP_ROUND = 2;

    /// <summary>Build 22621 (22H2) is where DWMWA_SYSTEMBACKDROP_TYPE became official.</summary>
    private static bool BackdropLikelySupported =>
        Environment.OSVersion.Version.Major >= 10 && Environment.OSVersion.Version.Build >= 22621;

    /// <summary>Rounded corners arrived with Windows 11 build 22000.</summary>
    private static bool CornersLikelySupported =>
        Environment.OSVersion.Version.Major >= 10 && Environment.OSVersion.Version.Build >= 22000;

    public static bool TryEnableDarkMode(IntPtr handle) =>
        TrySet(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, 1, "dark mode");

    /// <summary>Mica, for a long lived window such as Settings.</summary>
    public static bool TryEnableMainWindowBackdrop(IntPtr handle) =>
        BackdropLikelySupported && TrySet(handle, DWMWA_SYSTEMBACKDROP_TYPE, DWMSBT_MAINWINDOW, "mica backdrop");

    /// <summary>Acrylic, for a short lived window such as the track popup.</summary>
    public static bool TryEnableTransientBackdrop(IntPtr handle) =>
        BackdropLikelySupported && TrySet(handle, DWMWA_SYSTEMBACKDROP_TYPE, DWMSBT_TRANSIENTWINDOW, "acrylic backdrop");

    public static bool TrySetRoundedCorners(IntPtr handle) =>
        CornersLikelySupported && TrySet(handle, DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_ROUND, "rounded corners");

    private static bool TrySet(IntPtr handle, int attribute, int value, string what)
    {
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var result = NativeMethods.DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int));
            if (result == 0)
            {
                return true;
            }

            Logger.Info($"DWM {what} unavailable (HRESULT 0x{result:X8}); using the WPF fallback.");
            return false;
        }
        catch (Exception ex)
        {
            // DllNotFoundException / EntryPointNotFoundException on very old Windows.
            Logger.Info($"DWM {what} unavailable ({ex.GetType().Name}); using the WPF fallback.");
            return false;
        }
    }
}
