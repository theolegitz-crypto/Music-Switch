using MediaController.Core;
using Microsoft.Win32;

namespace MediaController.Services;

/// <summary>HKCU Run entry. ponytail: no service, no scheduled task, no elevation.</summary>
public sealed class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MediaController";

    public bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch (Exception ex)
        {
            Logger.Error("Could not read the Windows startup entry.", ex);
            return false;
        }
    }

    public bool Enable()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
            {
                Logger.Warn("Cannot enable startup: the executable path is unknown.");
                return false;
            }

            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                return false;
            }

            // Manual launch opens Settings. Windows startup explicitly requests background mode.
            // The executable is quoted so paths with spaces survive.
            var command = "\"" + exe + "\" --background";
            key.SetValue(ValueName, command, RegistryValueKind.String);
            Logger.Info($"Start with Windows enabled -> {command}");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("Could not enable the Windows startup entry.", ex);
            return false;
        }
    }

    public bool Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
            Logger.Info("Start with Windows disabled.");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("Could not remove the Windows startup entry.", ex);
            return false;
        }
    }

    public bool Apply(bool enabled) => enabled ? Enable() : Disable();
}
