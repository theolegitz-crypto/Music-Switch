using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MediaController.Core;
using Windows.Storage.Streams;

namespace MediaController.Services;

/// <summary>
/// Turns the GSMTC thumbnail into a frozen WPF ImageSource. The only source of artwork
/// in the whole app - nothing is ever fetched over the network.
/// </summary>
public sealed class MediaArtworkService
{
    /// <summary>Cover art is never shown larger than ~100 DIP, so decoding wider is wasted memory.</summary>
    private const int DecodeWidth = 256;

    private readonly object _gate = new();

    private string _cachedKey = string.Empty;
    private ImageSource? _cachedImage;

    /// <summary>Cache hit only. Lets the popup paint instantly instead of waiting for a decode.</summary>
    public ImageSource? TryGetCached(TrackInfo? track)
    {
        if (track is null)
        {
            return null;
        }

        lock (_gate)
        {
            return _cachedKey == track.ArtworkKey ? _cachedImage : null;
        }
    }

    /// <summary>Returns the artwork, or null when the player publishes none.</summary>
    public async Task<ImageSource?> GetAsync(TrackInfo? track)
    {
        if (track is null)
        {
            return null;
        }

        var key = track.ArtworkKey;

        lock (_gate)
        {
            if (_cachedKey == key)
            {
                return _cachedImage;
            }
        }

        var image = track.Thumbnail is null ? null : await DecodeAsync(track.Thumbnail).ConfigureAwait(false);

        lock (_gate)
        {
            // Remember misses too, so a player without artwork is not retried on every update.
            _cachedKey = key;
            _cachedImage = image;
        }

        return image;
    }

    public void Clear()
    {
        lock (_gate)
        {
            _cachedKey = string.Empty;
            _cachedImage = null;
        }
    }

    private static async Task<ImageSource?> DecodeAsync(IRandomAccessStreamReference reference)
    {
        try
        {
            byte[] bytes;

            // The WinRT stream is read out completely and closed here; nothing downstream
            // may depend on it staying alive.
            using (var stream = await reference.OpenReadAsync())
            {
                if (stream.Size == 0 || stream.Size > int.MaxValue)
                {
                    return null;
                }

                bytes = new byte[(int)stream.Size];

                using var reader = new DataReader(stream.GetInputStreamAt(0));
                await reader.LoadAsync((uint)stream.Size);
                reader.ReadBytes(bytes);
            }

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;   // decode now, then let the stream go
            image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            image.DecodePixelWidth = DecodeWidth;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();

            // Frozen, so the decode can happen off the UI thread and the result is shareable.
            image.Freeze();
            return image;
        }
        catch (Exception ex)
        {
            // A player can revoke the thumbnail between metadata read and open. Fall back to
            // the placeholder rather than failing the popup.
            Logger.Warn("Could not decode album artwork: " + ex.GetType().Name + ": " + ex.Message);
            return null;
        }
    }
}
