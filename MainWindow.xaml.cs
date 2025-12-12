using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using ColorThiefDotNet;
using Newtonsoft.Json.Linq;
using SpotifyAPI.Web;
using System.Windows.Threading;
using System.Diagnostics;
using System.Text;
using System.Net;
using System.Text.RegularExpressions; // Added for Regex
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Windows.Media;
using Windows.Media.Control;

using Forms = System.Windows.Forms;
using SpottyScreen.Classes;

namespace SpottyScreen
{
    public partial class MainWindow : Window
    {
        public SpotifyClient Spotify;

        // Low-level keyboard hook to intercept Win+Arrow keys
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private LowLevelKeyboardProc _proc;
        private IntPtr _hookID = IntPtr.Zero;

        private SpotifyManager manager;
        private List<LyricLine> lyrics = new List<LyricLine>();
        private int currentLyricIndex = -1;
        private DispatcherTimer progressTimer;
        private bool bannerShown = false;
        private FullTrack nextTrack = null; // Store next track info

        public MainWindow()
        {
            manager = new SpotifyManager(this);

            InitializeComponent();
            // Ensure the ScrollViewer width is available for MaxWidth calculation
            LyricsScrollViewer.Loaded += (s, e) => { /* Can trigger initial UI update if needed */ };

            // Hook into Windows messages to intercept Win+Arrow keys
            _proc = HookCallback;
            _hookID = SetHook(_proc);

            manager.Authenticate();
        }


        private FullTrack currentTrack;
        private bool isPolling = false; // Flag to prevent multiple polling loops

        public async void StartPolling()
        {
            if (isPolling) return;
            isPolling = true;
            Console.WriteLine("Starting playback polling...");

            while (isPolling)
            {
                if (Spotify == null)
                {
                    Console.WriteLine("Spotify client not initialized. Stopping polling.");
                    isPolling = false;
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        progressTimer?.Stop();
                        PlaybackProgressBar.Value = 0;
                    });
                    break;
                }

                CurrentlyPlaying playback = null;
                try
                {
                    playback = await Spotify.Player.GetCurrentlyPlaying(new PlayerCurrentlyPlayingRequest());

                    // Track changed
                    if (playback?.Item is FullTrack track && track.Id != currentTrack?.Id)
                    {
                        Console.WriteLine($"New track detected: {track.Name} by {string.Join(", ", track.Artists.Select(a => a.Name))}");
                        currentTrack = track;
                        
                        // Hide banner immediately when track changes
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            HideNextSongBanner();
                            bannerShown = false;
                        });
                        
                        ResetLyricsUI();
                        UpdateTrackInfoUI(track);
                        await LoadLyricsAsync(track);
                        InitializeProgressBar(track.DurationMs);
                        
                        // Fetch the next track in queue
                        await FetchNextTrack();
                    }
                    // Playback stopped or no item
                    else if (playback == null || playback.Item == null)
                    {
                        if (currentTrack != null)
                        {
                            Console.WriteLine("Playback stopped or unavailable.");
                            currentTrack = null;
                            ResetLyricsUI();
                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                progressTimer?.Stop();
                                PlaybackProgressBar.Value = 0;
                                HideNextSongBanner();
                                bannerShown = false;
                            });
                        }
                    }

                    // Update lyrics and progress bar
                    if (playback?.ProgressMs != null && currentTrack != null)
                    {
                        var playbackMs = playback.ProgressMs.Value;
                        var durationMs = currentTrack.DurationMs;
                        
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            PlaybackProgressBar.Value = playbackMs;
                            CurrentTimeLabel.Text = FormatTime(playbackMs); // Add this line
                        });

                        // Handle banner visibility based on remaining time
                        double remainingSeconds = (durationMs - playbackMs) / 1000.0;
                        
                        if (remainingSeconds <= 10 && remainingSeconds > 0 && !bannerShown && nextTrack != null)
                        {
                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                ShowNextSongBanner();
                                bannerShown = true;
                            });
                        }
                        else if (remainingSeconds > 10 && bannerShown)
                        {
                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                HideNextSongBanner();
                                bannerShown = false;
                            });
                        }

                        // Update banner countdown progress bar with smooth animation
                        if (bannerShown && remainingSeconds <= 10 && remainingSeconds > 0)
                        {
                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                // Progress from 0 to 1 over 10 seconds (inverted so it fills up)
                                double progress = (10 - remainingSeconds) / 10.0;
                                
                                // Smooth animation for the countdown bar
                                var animation = new DoubleAnimation
                                {
                                    To = progress,
                                    Duration = TimeSpan.FromMilliseconds(200),
                                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                                };
                                NextSongProgressBar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, animation);
                            });
                        }

                        var playbackTime = TimeSpan.FromMilliseconds(playbackMs);
                        SyncLyricsWithPlayback(playbackTime);
                    }

                    await Task.Delay(200);
                }
                catch (APIUnauthorizedException)
                {
                    Console.WriteLine("Access token expired during polling, attempting refresh...");
                    isPolling = false;
                    bool refreshed = await manager.RefreshAccessToken("41033dc65baf42e287b21398aafb4501", Properties.Settings.Default.SpotifyRefreshToken, new OAuthClient());
                    if (refreshed)
                    {
                        StartPolling();
                    }
                    break;
                }
                catch (APIException apiEx)
                {
                    Console.WriteLine($"Spotify API error during polling: {apiEx.Message} (Status: {apiEx.Response?.StatusCode})");
                    if (apiEx.Response?.StatusCode == (HttpStatusCode)429)
                        await Task.Delay(5000);
                    else
                        await Task.Delay(1000);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Unexpected error during polling: {ex.Message}");
                    await Task.Delay(2000);
                }
            }

            Console.WriteLine("Polling stopped.");
        }

        // Fetch the next track from Spotify queue
        private async Task FetchNextTrack()
        {
            try
            {
                var queue = await Spotify.Player.GetQueue();
                
                if (queue?.Queue != null && queue.Queue.Any())
                {
                    // Get the first item in the queue
                    if (queue.Queue.First() is FullTrack track)
                    {
                        nextTrack = track;
                        Console.WriteLine($"Next track in queue: {track.Name} by {string.Join(", ", track.Artists.Select(a => a.Name))}");
                        
                        // Update the banner UI
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            NextTrackName.Text = track.Name;
                            NextArtistName.Text = string.Join(", ", track.Artists.Select(a => a.Name));
                            
                            var imageUrl = track.Album.Images.FirstOrDefault()?.Url;
                            if (!string.IsNullOrEmpty(imageUrl))
                            {
                                var bitmap = new BitmapImage();
                                bitmap.BeginInit();
                                bitmap.UriSource = new Uri(imageUrl);
                                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                                bitmap.EndInit();
                                NextAlbumCover.Source = bitmap;
                            }
                        });
                    }
                }
                else
                {
                    Console.WriteLine("No next track in queue.");
                    nextTrack = null;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error fetching next track: {ex.Message}");
                nextTrack = null;
            }
        }

        // Initializes the progress bar for a new track and starts a DispatcherTimer for smooth updates
        private void InitializeProgressBar(int trackDurationMs)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                PlaybackProgressBar.Maximum = trackDurationMs;
                PlaybackProgressBar.Value = 0;
                
                // Update time labels
                CurrentTimeLabel.Text = "0:00";
                TotalTimeLabel.Text = FormatTime(trackDurationMs);

                if (progressTimer != null)
                {
                    progressTimer.Stop();
                    progressTimer.Tick -= ProgressTimer_Tick;
                }

                progressTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(50) // 50ms for smooth updates
                };
                progressTimer.Tick += ProgressTimer_Tick;
                progressTimer.Start();
            });
        }

        // Handler to smoothly increment the progress bar between Spotify polls
        private void ProgressTimer_Tick(object sender, EventArgs e)
        {
            if (PlaybackProgressBar.Value < PlaybackProgressBar.Maximum)
            {
                PlaybackProgressBar.Value = Math.Min(
                    PlaybackProgressBar.Value + progressTimer.Interval.TotalMilliseconds,
                    PlaybackProgressBar.Maximum);
                
                // Update current time label
                CurrentTimeLabel.Text = FormatTime(PlaybackProgressBar.Value);
            }
        }

        private void ResetLyricsUI()
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                // Stop any ongoing smooth scrolling animation
                CompositionTarget.Rendering -= SmoothScrollHandler;

                // Clear lyrics content and reset scroll position
                LyricsPanel.Children.Clear();
                LyricsScrollViewer.ScrollToVerticalOffset(0);

                // Reset the current lyric index
                currentLyricIndex = -1;
                lyrics.Clear(); // Clear the backing list as well
            });
        }

        private void SyncLyricsWithPlayback(TimeSpan playbackTime)
        {
            if (lyrics == null || lyrics.Count == 0) return; // No lyrics loaded

            // Find the index of the last lyric line whose time is less than or equal to the current playback time
            int newLyricIndex = -1;
            for (int i = lyrics.Count - 1; i >= 0; i--)
            {
                if (lyrics[i].Time <= playbackTime)
                {
                    newLyricIndex = i;
                    break;
                }
            }

            // Update UI only if the index has changed
            if (newLyricIndex != currentLyricIndex)
            {
                // Debugging: Log index change
                // Console.WriteLine($"Lyric index changing from {currentLyricIndex} to {newLyricIndex} at {playbackTime}");
                currentLyricIndex = newLyricIndex;

                // Update UI on the main thread
                Application.Current.Dispatcher.Invoke(() =>
                {
                    UpdateLyricsHighlighting(currentLyricIndex);
                    ScrollToCurrentLyric(currentLyricIndex);
                });
            }
        }

        // Renamed from UpdateLyricsDisplay to avoid confusion
        // This method now ONLY handles highlighting and assumes TextBlocks exist
        private void UpdateLyricsHighlighting(int highlightIndex)
        {
            for (int i = 0; i < LyricsPanel.Children.Count; i++)
            {
                if (LyricsPanel.Children[i] is TextBlock tb)
                {
                    bool isCurrent = (i == highlightIndex);
                    tb.Foreground = isCurrent ? Brushes.White : Brushes.Gray;
                    tb.FontSize = isCurrent ? 52 : 40;
                    tb.Opacity = isCurrent ? 1.0 : 0.5;
                    // Optional: Add a subtle effect like bolding the current line
                    // tb.FontWeight = isCurrent ? FontWeights.SemiBold : FontWeights.Normal;
                }
            }
        }

        // This method populates the LyricsPanel initially or after loading new lyrics
        private void PopulateLyricsPanel()
        {
            LyricsPanel.Children.Clear(); // Clear previous lyrics before adding new ones

            if (lyrics == null || lyrics.Count == 0)
            {
                ShowNoLyricsMessage();
                return;
            }

            // Calculate MaxWidth based on ScrollViewer, leave some padding
            double maxWidth = Math.Max(200, LyricsScrollViewer.ActualWidth - 40); // Ensure minimum width, subtract padding

            foreach (var lyric in lyrics)
            {
                var tb = new TextBlock
                {
                    Text = lyric.Text,
                    FontSize = 40, // Initial size for non-highlighted
                    Foreground = Brushes.Gray, // Initial color
                    Opacity = 0.5, // Initial opacity
                    FontFamily = new FontFamily("Segoe UI Variable"), // Consider making this configurable
                    TextWrapping = TextWrapping.Wrap, // Ensure wrapping is enabled
                    TextAlignment = TextAlignment.Center, // Center align the text within the block
                    HorizontalAlignment = HorizontalAlignment.Stretch, // Stretch to use available width (important for wrapping)
                    MaxWidth = maxWidth, // Apply calculated MaxWidth
                    Margin = new Thickness(0, 5, 0, 5) // Add some vertical spacing
                    // Removed TextTrimming.None - Wrap should handle it.
                };
                LyricsPanel.Children.Add(tb);
            }

            // After populating, ensure the initial highlight state is correct
            UpdateLyricsHighlighting(currentLyricIndex);
            ScrollToCurrentLyric(currentLyricIndex); // Scroll to the correct position if resuming playback mid-song
        }

        private void UpdateTrackInfoUI(FullTrack track)
        {
            TrackName.Text = track.Name;
            ArtistName.Text = string.Join(", ", track.Artists.Select(a => a.Name));
            AlbumName.Text = track.Album.Name;

            var imageUrl = track.Album.Images.FirstOrDefault()?.Url;
            if (string.IsNullOrEmpty(imageUrl))
                return;

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(imageUrl);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();

            bitmap.DownloadCompleted += async (s, e) =>
            {
                AlbumCover.ImageSource = bitmap;
                StartImageTransition();

                // Extract color palette
                using (var webClient = new WebClient())
                {
                    byte[] data = await webClient.DownloadDataTaskAsync(imageUrl);
                    using (var ms = new MemoryStream(data))
                    using (var bmp = new System.Drawing.Bitmap(ms))
                    {
                        var colorThief = new ColorThief();
                        var palette = await Task.Run(() => colorThief.GetPalette(bmp, 5, 10));

                        if (palette != null && palette.Any())
                        {
                            // Get the two darkest colors
                            var darkColors = palette
                                .Select(p => p.Color)
                                .OrderBy(c => 0.299 * c.R + 0.587 * c.G + 0.114 * c.B)
                                .Take(2)
                                .ToList();

                            if (darkColors.Count >= 2)
                            {
                                var gradient = new LinearGradientBrush
                                {
                                    StartPoint = new Point(0, 0),
                                    EndPoint = new Point(1, 1),
                                    GradientStops = new GradientStopCollection
                            {
                                new GradientStop(
                                    System.Windows.Media.Color.FromRgb(darkColors[0].R, darkColors[0].G, darkColors[0].B), 0),
                                new GradientStop(
                                    System.Windows.Media.Color.FromRgb(darkColors[1].R, darkColors[1].G, darkColors[1].B), 1)
                            }
                                };

                                BlurredBackground.Background = gradient;
                            }
                        }

                        // Set progress bar color separately
                        await SetProgressBarColorFromUrl(imageUrl);
                    }
                }
            };

            AlbumCover.ImageSource = bitmap;
        }

        /// <summary>
        /// Downloads the album‐art bytes, creates a System.Drawing.Bitmap for ColorThief,
        /// finds the dominant color, and then sets the ProgressBar foreground to that color.
        /// </summary>
        private async Task SetProgressBarColorFromUrl(string imageUrl)
        {
            try
            {
                using (var webClient = new WebClient())
                {
                    byte[] data = await webClient.DownloadDataTaskAsync(imageUrl);
                    using (var ms = new MemoryStream(data))
                    using (var bmp = new System.Drawing.Bitmap(ms))
                    {
                        var colorThief = new ColorThief();
                        var palette = await Task.Run(() => colorThief.GetPalette(bmp, 5, 10));

                        if (palette != null && palette.Any())
                        {
                            var white = new { R = 255, G = 255, B = 255 };

                            // Filter colors not too close to white
                            var filteredColors = palette
                                .Select(p => p.Color) // Assuming p.Color is a ColorThief color with R, G, B
                                .Where(c =>
                                {
                                    double distanceToWhite = Math.Sqrt(
                                        Math.Pow(c.R - white.R, 2) +
                                        Math.Pow(c.G - white.G, 2) +
                                        Math.Pow(c.B - white.B, 2));
                                    return distanceToWhite > 100; // if >100, not too close to white
                                });

                            // Pick the most prominent color based on luminance
                            var chosen = filteredColors
                                .OrderByDescending(c => 0.299 * c.R + 0.587 * c.G + 0.114 * c.B)
                                .FirstOrDefault();

                            // Fallback: choose the lightest color in the palette if no suitable color found
                            if (chosen.R == 0 && chosen.G == 0 && chosen.B == 0)
                            {
                                // No valid color found, pick gray as fallback
                                chosen = new ColorThiefDotNet.Color() { R = 169, G = 169, B = 169 };
                            }

                            // Convert ColorThief color to WPF SolidColorBrush
                            var brush = new SolidColorBrush(
                                System.Windows.Media.Color.FromRgb(chosen.R, chosen.G, chosen.B));
                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                PlaybackProgressBar.Foreground = brush;
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to extract color for progress bar: {ex.Message}");
            }
        }

        private void StartImageTransition()
        {
            // Create your image transition animation here.
            // For example, fade the image in and out or create a zoom-in effect.
            var animation = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(1));
            AlbumCover.BeginAnimation(OpacityProperty, animation);
            BlurredBackground.BeginAnimation(OpacityProperty, animation);
        }

        private async Task LoadLyricsAsync(FullTrack track)
        {
            var artist = track.Artists.FirstOrDefault()?.Name ?? "";
            var title = track.Name;
            var album = track.Album.Name; // Using album name might improve accuracy

            Console.WriteLine($"Searching lyrics for: {title} - {artist} ({album})");

            lyrics.Clear(); // Clear previous lyrics list
            currentLyricIndex = -1; // Reset index

            // Use a more robust URL encoding library if available, but EscapeDataString is usually sufficient
            var url = $"https://lrclib.net/api/search?artist_name={Uri.EscapeDataString(artist)}&track_name={Uri.EscapeDataString(title)}&album_name={Uri.EscapeDataString(album)}";

            using (var client = new HttpClient())
            {
                // Add a user-agent, some APIs require it
                client.DefaultRequestHeaders.UserAgent.ParseAdd("SpottyScreen/1.0");
                try
                {
                    var response = await client.GetAsync(url);
                    response.EnsureSuccessStatusCode(); // Throw exception for bad status codes
                    var json = await response.Content.ReadAsStringAsync();

                    var data = JArray.Parse(json);

                    if (data.Count > 0)
                    {
                        // Prioritize synced lyrics, fall back to plain lyrics if needed
                        var syncedLyricsToken = data[0]["syncedLyrics"]?.ToString();
                        var plainLyricsToken = data[0]["plainLyrics"]?.ToString();

                        if (!string.IsNullOrEmpty(syncedLyricsToken))
                        {
                            Console.WriteLine("Found synced lyrics.");
                            // Regex to capture minutes, seconds (including fractional), and text
                            var regex = new Regex(@"\[(\d{2}):(\d{2}\.\d{2,3})\](.*)");
                            var matches = regex.Matches(syncedLyricsToken);

                            foreach (Match match in matches)
                            {
                                if (match.Groups.Count >= 4)
                                {
                                    try
                                    {
                                        int minutes = int.Parse(match.Groups[1].Value);
                                        // Use decimal for precision with fractional seconds
                                        decimal seconds = decimal.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                                        string text = match.Groups[3].Value.Trim();

                                        // Convert decimal seconds to TimeSpan
                                        long ticks = (long)(minutes * 60 * TimeSpan.TicksPerSecond + seconds * TimeSpan.TicksPerSecond);
                                        var time = TimeSpan.FromTicks(ticks);

                                        // Add only if the line has text content
                                        if (!string.IsNullOrWhiteSpace(text))
                                        {
                                            lyrics.Add(new LyricLine { Time = time, Text = text });
                                        }
                                    }
                                    catch (FormatException ex)
                                    {
                                        Console.WriteLine($"Error parsing lyric timestamp/format: {match.Value}. Exception: {ex.Message}");
                                    }
                                    catch (Exception ex) // Catch other potential parsing errors
                                    {
                                        Console.WriteLine($"Generic error parsing lyric line: {match.Value}. Exception: {ex.Message}");
                                    }
                                }
                            }
                            // Sort by time just in case the source isn't perfectly ordered
                            lyrics.Sort((a, b) => a.Time.CompareTo(b.Time));
                            Console.WriteLine($"Successfully parsed {lyrics.Count} synced lyric lines.");
                        }
                        else
                        {
                            Console.WriteLine("No lyrics found in the first result.");
                            // No need to call ShowNoLyricsMessage here, PopulateLyricsPanel will handle empty list
                        }
                    }
                    else
                    {
                        Console.WriteLine("No results found from lrclib API.");
                        // PopulateLyricsPanel will handle empty list
                    }
                }
                catch (HttpRequestException httpEx)
                {
                    Console.WriteLine($"HTTP error fetching lyrics: {httpEx.Message} (URL: {url})");
                    // PopulateLyricsPanel will handle empty list
                }
                catch (Newtonsoft.Json.JsonException jsonEx)
                {
                    Console.WriteLine($"Error parsing JSON response from lrclib: {jsonEx.Message}");
                    // PopulateLyricsPanel will handle empty list
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Unexpected error fetching/parsing lyrics: {ex.Message}");
                    // PopulateLyricsPanel will handle empty list
                }
            }

            // Update the UI panel with the loaded lyrics (or "No lyrics" message)
            Application.Current.Dispatcher.Invoke(PopulateLyricsPanel);

            // No need to call StartLyricSync - the main polling loop handles syncing
            // StartLyricSync(); // Removed
        }

        private void ShowNoLyricsMessage()
        {
            // Ensure this runs on the UI thread
            Application.Current.Dispatcher.Invoke(() =>
            {
                LyricsPanel.Children.Clear(); // Clear any previous content

                // Consider making the "No lyrics" message style consistent
                double maxWidth = Math.Max(200, LyricsScrollViewer.ActualWidth - 40);

                var noLyricsTextBlock = new TextBlock
                {
                    Text = "Lyrics not found for this track.",
                    Foreground = Brushes.Gray, // Use a less prominent color
                    FontSize = 30, // Slightly smaller than regular lyrics
                    FontFamily = new FontFamily("Segoe UI Variable"),
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Stretch, // Stretch to center properly
                    VerticalAlignment = VerticalAlignment.Center, // Center vertically if panel allows
                    MaxWidth = maxWidth, // Apply width constraint
                    Margin = new Thickness(20) // Add some margin
                };
                LyricsPanel.Children.Add(noLyricsTextBlock);
            });
        }


        // Removed the parameterless UpdateLyricsDisplay as it's redundant with SyncLyricsWithPlayback
        // private async void UpdateLyricsDisplay() { ... }


        private void ScrollToCurrentLyric(int currentIndex)
        {
            // Basic checks
            if (LyricsScrollViewer == null || LyricsPanel.Children.Count == 0 || currentIndex < 0 || currentIndex >= LyricsPanel.Children.Count)
            {
                // If index is -1 (before first lyric), scroll to top
                if (currentIndex == -1)
                {
                    Application.Current.Dispatcher.InvokeAsync(() => LyricsScrollViewer.ScrollToVerticalOffset(0), DispatcherPriority.Background);
                }
                return;
            }


            var currentElement = LyricsPanel.Children[currentIndex] as FrameworkElement; // Use FrameworkElement for ActualHeight

            if (currentElement == null) return;

            // Use Dispatcher.InvokeAsync with Background priority to ensure layout is updated
            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (!currentElement.IsLoaded) return; // Check if element is ready

                try
                {
                    // Calculate the target vertical offset to center the item
                    double currentOffset = LyricsScrollViewer.VerticalOffset;
                    double viewportHeight = LyricsScrollViewer.ViewportHeight;

                    // Get the position of the top of the element relative to the ScrollViewer content area
                    GeneralTransform transform = currentElement.TransformToAncestor(LyricsPanel); // Transform relative to the panel first
                    Point elementTopInPanel = transform.Transform(new Point(0, 0));

                    // Center position calculation
                    double targetOffset = elementTopInPanel.Y - (viewportHeight / 2.0) + (currentElement.ActualHeight / 2.0);

                    // Clamp the target offset to be within valid scroll range [0, ScrollableHeight]
                    targetOffset = Math.Max(0, Math.Min(targetOffset, LyricsScrollViewer.ScrollableHeight));

                    // Stop previous animation if running
                    CompositionTarget.Rendering -= SmoothScrollHandler;

                    // Simple animation loop using CompositionTarget.Rendering
                    double startOffset = currentOffset;
                    double distance = targetOffset - startOffset;
                    double duration = 0.3; // Animation duration in seconds
                    double startTime = -1;

                    SmoothScrollHandler = (s, e) =>
                    {
                        if (startTime < 0) startTime = (e as RenderingEventArgs)?.RenderingTime.TotalSeconds ?? 0;

                        double elapsed = ((e as RenderingEventArgs)?.RenderingTime.TotalSeconds ?? startTime) - startTime;
                        double progress = Math.Min(1.0, elapsed / duration); // Normalized progress [0, 1]

                        // Simple easing (quadratic ease-out)
                        double easeProgress = 1.0 - Math.Pow(1.0 - progress, 2);

                        double newOffset = startOffset + distance * easeProgress;

                        LyricsScrollViewer.ScrollToVerticalOffset(newOffset);

                        // Stop when close enough or duration exceeded
                        if (progress >= 1.0 || Math.Abs(targetOffset - newOffset) < 1.0)
                        {
                            LyricsScrollViewer.ScrollToVerticalOffset(targetOffset); // Ensure final position
                            CompositionTarget.Rendering -= SmoothScrollHandler;
                            SmoothScrollHandler = null; // Clear handler reference
                        }
                    };

                    CompositionTarget.Rendering += SmoothScrollHandler;
                }
                catch (InvalidOperationException invEx)
                {
                    // This can happen if the element is not visually connected correctly
                    Console.WriteLine($"Scroll error (InvalidOperationException): {invEx.Message}. Element might not be in visual tree.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Generic scroll error: {ex.Message}");
                }
            }, DispatcherPriority.Background); // Use Background priority
        }

        // Store the handler reference to remove it correctly
        private EventHandler SmoothScrollHandler;


        // Removed - Timer logic replaced by main polling loop
        // private void StartLyricSync() { ... }

        // LyricLine class remains the same
        private class LyricLine
        {
            public TimeSpan Time { get; set; }
            public string Text { get; set; }

            public override string ToString() // For debugging
            {
                return $"[{Time:mm\\:ss\\.ff}] {Text}";
            }
        }

        private void ShowNextSongBanner()
        {
            NextSongBanner.Visibility = Visibility.Visible;
            NextSongProgressBar.Value = 0; // Reset progress bar
            
            // Slide in animation from the right
            var slideIn = new DoubleAnimation
            {
                From = 100,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(400),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            
            var fadeIn = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(400)
            };
            
            var translateTransform = new TranslateTransform();
            NextSongBanner.RenderTransform = translateTransform;
            
            translateTransform.BeginAnimation(TranslateTransform.XProperty, slideIn);
            NextSongBanner.BeginAnimation(OpacityProperty, fadeIn);
        }
        
        private void HideNextSongBanner()
        {
            var fadeOut = new DoubleAnimation
            {
                From = 1,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(300)
            };
            
            fadeOut.Completed += (s, e) => 
            {
                NextSongBanner.Visibility = Visibility.Collapsed;
                NextSongProgressBar.Value = 0; // Reset progress when hiding
            };
            NextSongBanner.BeginAnimation(OpacityProperty, fadeOut);
        }

        // Add this method to format time in mm:ss format
        private string FormatTime(double milliseconds)
        {
            var timeSpan = TimeSpan.FromMilliseconds(milliseconds);
            return $"{(int)timeSpan.TotalMinutes}:{timeSpan.Seconds:D2}";
        }

        #region Windows Hook for disabling Win+Arrow keys

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private IntPtr SetHook(LowLevelKeyboardProc proc)
        {
            using (Process curProcess = Process.GetCurrentProcess())
            using (ProcessModule curModule = curProcess.MainModule)
            {
                return SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(curModule.ModuleName), 0);
            }
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && wParam == (IntPtr)WM_KEYDOWN)
            {
                int vkCode = Marshal.ReadInt32(lParam);
        
                // Check if Windows key is pressed (VK_LWIN = 0x5B, VK_RWIN = 0x5C)
                bool winKeyPressed = (GetAsyncKeyState(0x5B) & 0x8000) != 0 || (GetAsyncKeyState(0x5C) & 0x8000) != 0;
        
                // VK_LEFT = 0x25, VK_RIGHT = 0x27, VK_UP = 0x26, VK_DOWN = 0x28
                if (winKeyPressed && (vkCode == 0x25 || vkCode == 0x27 || vkCode == 0x26 || vkCode == 0x28))
                {
                    // Block Windows+Arrow keys for this app
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        if (vkCode == 0x25) // Left arrow
                        {
                            MoveWindowToPreviousMonitor();
                        }
                        else if (vkCode == 0x27) // Right arrow
                        {
                            MoveWindowToNextMonitor();
                        }
                    });
                    return (IntPtr)1; // Block the key
                }
            }
            return CallNextHookEx(_hookID, nCode, wParam, lParam);
        }

        #endregion

        #region Window Controls and Monitor Navigation

        private void Window_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            // Show window controls on mouse enter with fade-in animation
            var fadeIn = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(200)
            };
            WindowControlsPanel.Visibility = Visibility.Visible;
            WindowControlsPanel.BeginAnimation(OpacityProperty, fadeIn);
        }

        private void Window_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            // Hide window controls on mouse leave with fade-out animation
            var fadeOut = new DoubleAnimation
            {
                From = 1,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(200)
            };
            fadeOut.Completed += (s, args) => WindowControlsPanel.Visibility = Visibility.Collapsed;
            WindowControlsPanel.BeginAnimation(OpacityProperty, fadeOut);
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void MoveToNextMonitor_Click(object sender, RoutedEventArgs e)
        {
            MoveWindowToNextMonitor();
        }

        private void MoveToPreviousMonitor_Click(object sender, RoutedEventArgs e)
        {
            MoveWindowToPreviousMonitor();
        }

        private void MoveWindowToNextMonitor()
        {
            var screens = Forms.Screen.AllScreens.OrderBy(s => s.Bounds.Left).ToList();
            var currentScreen = Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle);
    
            int currentIndex = screens.IndexOf(currentScreen);
            int nextIndex = (currentIndex + 1) % screens.Count;
    
            MoveToScreen(screens[nextIndex]);
        }

        private void MoveWindowToPreviousMonitor()
        {
            var screens = Forms.Screen.AllScreens.OrderBy(s => s.Bounds.Left).ToList();
            var currentScreen = Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle);
    
            int currentIndex = screens.IndexOf(currentScreen);
            int previousIndex = (currentIndex - 1 + screens.Count) % screens.Count;
    
            MoveToScreen(screens[previousIndex]);
        }

        private void MoveToScreen(Forms.Screen screen)
        {
            // Temporarily change to Normal state to move
            var previousState = WindowState;
            WindowState = WindowState.Normal;

            // Position window to cover the entire screen
            Left = screen.Bounds.Left;
            Top = screen.Bounds.Top;
            Width = screen.Bounds.Width;
            Height = screen.Bounds.Height;
    
            // Return to maximized state on the new screen
            WindowState = WindowState.Maximized;
        }

        #endregion
    }
}