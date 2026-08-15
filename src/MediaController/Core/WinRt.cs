using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;

namespace MediaController.Core;

/// <summary>
/// Awaits WinRT operations without ever throwing or hanging: a dead player can leave a
/// GSMTC call pending forever, and that must not block the fallback path.
/// </summary>
public static class WinRt
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    public static async Task<bool> TryBoolAsync(Func<IAsyncOperation<bool>> start, string context)
    {
        try
        {
            return await start().AsTask().WaitAsync(Timeout).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Warn($"{context} failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    public static async Task<T?> TryAsync<T>(Func<IAsyncOperation<T>> start, string context)
        where T : class
    {
        try
        {
            return await start().AsTask().WaitAsync(Timeout).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Warn($"{context} failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}
