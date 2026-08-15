using System;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Storage.Streams;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Media.Imaging;

namespace MediaController.GameBar
{
    public sealed partial class OverlayPage : Page
    {
        private readonly DesktopBridgeClient _bridge = new DesktopBridgeClient();
        private readonly DispatcherTimer _hideTimer = new DispatcherTimer();
        private int _generation;

        public OverlayPage()
        {
            InitializeComponent();
            _hideTimer.Tick += OnHideTimer;
            Unloaded += OnUnloaded;
            _bridge.MessageReceived += OnBridgeMessage;
            _bridge.Start();
        }

        private void OnBridgeMessage(string json)
        {
            var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async delegate
            {
                await RenderMessageAsync(json);
            });
        }

        private async Task RenderMessageAsync(string json)
        {
            JsonObject data;
            try
            {
                data = JsonObject.Parse(json);
            }
            catch
            {
                return;
            }

            var generation = ++_generation;
            var title = GetString(data, "title");
            var artist = GetString(data, "artist");
            var player = GetString(data, "player");
            var status = GetString(data, "status");
            var playing = GetBoolean(data, "isPlaying");
            var durationMs = (int)Math.Max(500, Math.Min(10000, GetNumber(data, "durationMs", 2000)));
            var artwork = GetString(data, "artworkBase64");

            if (string.IsNullOrWhiteSpace(title))
            {
                title = string.IsNullOrWhiteSpace(artist) ? "Unknown track" : artist;
                artist = string.Empty;
            }

            TitleText.Text = title;
            ArtistText.Text = artist;
            ArtistText.Visibility = string.IsNullOrWhiteSpace(artist) ? Visibility.Collapsed : Visibility.Visible;
            SourceText.Text = (playing ? "▶" : "⏸") + "  " + status + "  ·  " + player;

            if (!string.IsNullOrWhiteSpace(artwork))
            {
                var image = await DecodeArtworkAsync(artwork);
                if (generation == _generation && image != null)
                {
                    ArtworkBrush.ImageSource = image;
                    ArtworkBorder.Visibility = Visibility.Visible;
                }
            }
            else
            {
                ArtworkBrush.ImageSource = null;
                ArtworkBorder.Visibility = Visibility.Collapsed;
            }

            FadeTo(1, 120);
            _hideTimer.Stop();
            _hideTimer.Interval = TimeSpan.FromMilliseconds(durationMs);
            _hideTimer.Start();
        }

        private void OnHideTimer(object sender, object e)
        {
            _hideTimer.Stop();
            FadeTo(0, 180);
        }

        private void FadeTo(double value, int milliseconds)
        {
            var animation = new DoubleAnimation
            {
                To = value,
                Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds))
            };

            Storyboard.SetTarget(animation, RootCard);
            Storyboard.SetTargetProperty(animation, "Opacity");

            var storyboard = new Storyboard();
            storyboard.Children.Add(animation);
            storyboard.Begin();
        }

        private static async Task<BitmapImage> DecodeArtworkAsync(string encoded)
        {
            try
            {
                var bytes = Convert.FromBase64String(encoded);
                var stream = new InMemoryRandomAccessStream();
                var writer = new DataWriter(stream);
                try
                {
                    writer.WriteBytes(bytes);
                    await writer.StoreAsync();
                    writer.DetachStream();
                }
                finally
                {
                    writer.Dispose();
                }

                stream.Seek(0);
                var image = new BitmapImage();
                await image.SetSourceAsync(stream);
                stream.Dispose();
                return image;
            }
            catch
            {
                return null;
            }
        }

        private static string GetString(JsonObject data, string name)
        {
            try
            {
                return data.ContainsKey(name) ? data.GetNamedString(name) : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static bool GetBoolean(JsonObject data, string name)
        {
            try
            {
                return data.ContainsKey(name) && data.GetNamedBoolean(name);
            }
            catch
            {
                return false;
            }
        }

        private static double GetNumber(JsonObject data, string name, double fallback)
        {
            try
            {
                return data.ContainsKey(name) ? data.GetNamedNumber(name) : fallback;
            }
            catch
            {
                return fallback;
            }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _hideTimer.Stop();
            _bridge.MessageReceived -= OnBridgeMessage;
            _bridge.Dispose();
        }
    }
}
