using System.IO;
using MediaController.Core;
using MediaController.Native;
using Velopack.Windows;

namespace MediaController.Services;

/// <summary>
/// Repairs shortcuts created by older releases. v0.4.0 was shipped before the application
/// icon and manual-launch behaviour existed, and Windows may keep those old .lnk properties
/// through an in-place update. This explicitly clears shortcut arguments and assigns the
/// current executable as the icon source.
/// </summary>
public static class ShortcutRepairService
{
    public static void TryRepairInstalledShortcuts()
    {
        try
        {
            if (!LooksLikeVelopackInstall())
            {
                return;
            }

            var exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath))
            {
                return;
            }

            var shortcutIcon = Path.Combine(AppContext.BaseDirectory, "Assets", "MediaControllerShortcut.ico");
            if (!File.Exists(shortcutIcon))
            {
                shortcutIcon = exePath;
            }

#pragma warning disable CS0618 // Velopack exposes this legacy helper specifically for custom shortcut repair.
            var shortcuts = new Shortcuts();
            shortcuts.CreateShortcut(
                relativeExeName: Path.GetFileName(exePath),
                locations: ShortcutLocation.Desktop | ShortcutLocation.StartMenuRoot,
                updateOnly: true,
                programArguments: string.Empty,
                icon: shortcutIcon);
#pragma warning restore CS0618

            // Explorer aggressively caches shortcut/exe icons. Tell the shell that icon
            // associations changed so the new purple icon becomes visible without a reboot.
            NativeMethods.SHChangeNotify(
                NativeMethods.SHCNE_ASSOCCHANGED,
                NativeMethods.SHCNF_IDLIST,
                IntPtr.Zero,
                IntPtr.Zero);

            Logger.Info("Desktop/Start Menu shortcuts repaired and icon cache refresh requested.");
        }
        catch (Exception ex)
        {
            // Cosmetic convenience only; never block app startup for shortcut repair.
            Logger.Warn("Shortcut repair failed: " + ex.Message);
        }
    }

    private static bool LooksLikeVelopackInstall()
    {
        try
        {
            var currentDirectory = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
            var root = currentDirectory.Parent;
            return root is not null && File.Exists(Path.Combine(root.FullName, "sq.version"));
        }
        catch
        {
            return false;
        }
    }
}
