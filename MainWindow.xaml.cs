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
using System.Windows.Threading;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Net;
using Forms = System.Windows.Forms;
using SpottyScreen.Classes;

namespace SpottyScreen
{
    public partial class MainWindow : Window
    {
        private readonly IMusicApiManager _musicManager;
        private TrackInfo _currentTrack;
        private TrackInfo _nextTrack;

        // Low-level keyboard hook
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private readonly LowLevelKeyboardProc _proc;
        private readonly IntPtr _hookID = IntPtr.Zero;

        private readonly List<LyricLine> _lyrics = new List<LyricLine>();
        private int _currentLyricIndex = -1;
        private DispatcherTimer _progressTimer;
        private bool _bannerShown;

        public MainWindow()
        {
            InitializeComponent();

            _musicManager = new SpotifyManager();
            SubscribeToMusicManagerEvents();

            LyricsScrollViewer.Loaded += (s, e) => { /* Can trigger initial UI update if needed */ };

            _proc = HookCallback;
            _hookID = SetHook(_proc);

            _musicManager.Authenticate();
        }

        private void SubscribeToMusicManagerEvents()
        {
            _musicManager.Authenticated += OnAuthenticated;
            _musicManager.AuthenticationStarted += OnAuthenticationStarted;
            _musicManager.AuthenticationFinished += OnAuthenticationFinished;
            _musicManager.ReauthenticationNeeded += OnReauthenticationNeeded;

            _musicManager.TrackChanged += OnTrackChanged;
            _musicManager.PlaybackUpdated += OnPlaybackUpdated;
            _musicManager.PlaybackStopped += OnPlaybackStopped;
            _musicManager.NextTrackAvailable += OnNextTrackAvailable;
        }

        #region Music Manager Event Handlers

        private void OnAuthenticated()
        {
            _musicManager.StartPolling();
        }

        private void OnAuthenticationStarted()
        {
            Application.Current.Dispatcher.Invoke(() => WindowState = WindowState.Minimized);
        }

        private void OnAuthenticationFinished(bool success, string error)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                WindowState = WindowState.Maximized;
                if (!success)
                {
                    MessageBox.Show(error, "Authentication Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            });
        }

        private void OnReauthenticationNeeded()
        {
            // Fired when the refresh token is invalid
            Application.Current.Dispatcher.Invoke(() =>
            {
                MessageBox.Show("Spotify session expired. Please log in again.", "Authentication Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                _musicManager.Authenticate();
            });
        }

        private async void OnTrackChanged(TrackInfo track)
        {
            _currentTrack = track;

            await Application.Current.Dispatcher.Invoke(async () =>
            {
                HideNextSongBanner();
                _bannerShown = false;
                ResetLyricsUI();
                UpdateTrackInfoUI(track);
                await LoadLyricsAsync(track);
                InitializeProgressBar(track.DurationMs);
            });
        }

        private void OnPlaybackUpdated(PlaybackState playbackState)
        {
            if (playbackState?.Track == null) return;

            var playbackMs = playbackState.ProgressMs.GetValueOrDefault();
            var durationMs = playbackState.Track.DurationMs;

            Application.Current.Dispatcher.Invoke(() =>
            {
                PlaybackProgressBar.Value = playbackMs;
                CurrentTimeLabel.Text = FormatTime(playbackMs);
            });

            // Handle banner visibility
            var remainingSeconds = (durationMs - playbackMs) / 1000.0;
            if (remainingSeconds <= 10 && remainingSeconds > 0 && !_bannerShown && _nextTrack != null)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    ShowNextSongBanner();
                    _bannerShown = true;
                });
            }
            else if (remainingSeconds > 10 && _bannerShown)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    HideNextSongBanner();
                    _bannerShown = false;
                });
            }

            // Update banner countdown
            if (_bannerShown && remainingSeconds <= 10 && remainingSeconds > 0)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    var progress = (10 - remainingSeconds) / 10.0;
                    var animation = new DoubleAnimation(progress, TimeSpan.FromMilliseconds(200))
                        { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
                    NextSongProgressBar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, animation);
                });
            }

            SyncLyricsWithPlayback(TimeSpan.FromMilliseconds(playbackMs));
        }

        private void OnPlaybackStopped()
        {
            _currentTrack = null;
            Application.Current.Dispatcher.Invoke(() =>
            {
                _progressTimer?.Stop();
                PlaybackProgressBar.Value = 0;
                HideNextSongBanner();
                _bannerShown = false;
                ResetLyricsUI();
            });
        }

        private void OnNextTrackAvailable(TrackInfo nextTrack)
        {
            _nextTrack = nextTrack;
            if (nextTrack == null) return;

            Application.Current.Dispatcher.Invoke(() =>
            {
                NextTrackName.Text = nextTrack.Name;
                NextArtistName.Text = string.Join(", ", nextTrack.Artists);

                if (!string.IsNullOrEmpty(nextTrack.ImageUrl))
                {
                    var bitmap = new BitmapImage(new Uri(nextTrack.ImageUrl));
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    NextAlbumCover.Source = bitmap;
                }
            });
        }

        #endregion

        #region UI Update Methods

        private void UpdateTrackInfoUI(TrackInfo track)
        {
            if (track == null) return;

            TrackName.Text = track.Name;
            ArtistName.Text = string.Join(", ", track.Artists);
            AlbumName.Text = track.AlbumName;

            if (string.IsNullOrEmpty(track.ImageUrl)) return;

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(track.ImageUrl);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();

            bitmap.DownloadCompleted += async (s, e) =>
            {
                AlbumCover.ImageSource = bitmap;
                StartImageTransition();

                using (var webClient = new WebClient())
                {
                    var data = await webClient.DownloadDataTaskAsync(track.ImageUrl);
                    using (var ms = new MemoryStream(data))
                    using (var bmp = new System.Drawing.Bitmap(ms))
                    {
                        var colorThief = new ColorThief();
                        var palette = await Task.Run(() => colorThief.GetPalette(bmp, 5, 10));

                        if (palette?.Any() == true)
                        {
                            var darkColors = palette.Select(p => p.Color)
                                .OrderBy(c => 0.299 * c.R + 0.587 * c.G + 0.114 * c.B)
                                .Take(2).ToList();

                            if (darkColors.Count >= 2)
                            {
                                var gradient = new LinearGradientBrush(
                                    System.Windows.Media.Color.FromRgb(darkColors[0].R, darkColors[0].G, darkColors[0].B),
                                    System.Windows.Media.Color.FromRgb(darkColors[1].R, darkColors[1].G, darkColors[1].B),
                                    new Point(0, 0), new Point(1, 1));
                                BlurredBackground.Background = gradient;
                            }
                        }
                        await SetProgressBarColorFromUrl(track.ImageUrl);
                    }
                }
            };
            AlbumCover.ImageSource = bitmap;
        }

        private void InitializeProgressBar(int trackDurationMs)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                PlaybackProgressBar.Maximum = trackDurationMs;
                PlaybackProgressBar.Value = 0;
                CurrentTimeLabel.Text = "0:00";
                TotalTimeLabel.Text = FormatTime(trackDurationMs);

                _progressTimer?.Stop();
                _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
                _progressTimer.Tick += ProgressTimer_Tick;
                _progressTimer.Start();
            });
        }

        private void ProgressTimer_Tick(object sender, EventArgs e)
        {
            if (PlaybackProgressBar.Value < PlaybackProgressBar.Maximum)
            {
                PlaybackProgressBar.Value = Math.Min(PlaybackProgressBar.Value + _progressTimer.Interval.TotalMilliseconds, PlaybackProgressBar.Maximum);
                CurrentTimeLabel.Text = FormatTime(PlaybackProgressBar.Value);
            }
        }

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

                        if (palette?.Any() == true)
                        {
                            var chosen = palette.Select(p => p.Color)
                                .Where(c => Math.Sqrt(Math.Pow(c.R - 255, 2) + Math.Pow(c.G - 255, 2) + Math.Pow(c.B - 255, 2)) > 100)
                                .OrderByDescending(c => 0.299 * c.R + 0.587 * c.G + 0.114 * c.B)
                                .FirstOrDefault();

                            if (chosen.R == 0 && chosen.G == 0 && chosen.B == 0)
                            {
                                chosen = new ColorThiefDotNet.Color { R = 169, G = 169, B = 169 };
                            }

                            var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(chosen.R, chosen.G, chosen.B));
                            Application.Current.Dispatcher.Invoke(() => PlaybackProgressBar.Foreground = brush);
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
            var animation = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(1));
            AlbumCover.BeginAnimation(OpacityProperty, animation);
            BlurredBackground.BeginAnimation(OpacityProperty, animation);
        }

        #endregion

        #region Lyrics Handling

        private async Task LoadLyricsAsync(TrackInfo track)
        {
            var artist = track.Artists.FirstOrDefault() ?? "";
            var title = track.Name;
            var album = track.AlbumName;

            Console.WriteLine($"Searching lyrics for: {title} - {artist} ({album})");

            _lyrics.Clear();
            _currentLyricIndex = -1;

            var url = $"https://lrclib.net/api/search?artist_name={Uri.EscapeDataString(artist)}&track_name={Uri.EscapeDataString(title)}&album_name={Uri.EscapeDataString(album)}";

            try
            {
                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("SpottyScreen/1.0");
                    var json = await client.GetStringAsync(url);
                    var data = JArray.Parse(json);

                    if (data.Count > 0)
                    {
                        var syncedLyricsToken = data[0]["syncedLyrics"]?.ToString();
                        if (!string.IsNullOrEmpty(syncedLyricsToken))
                        {
                            ParseSyncedLyrics(syncedLyricsToken);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error fetching/parsing lyrics: {ex.Message}");
            }

            Application.Current.Dispatcher.Invoke(PopulateLyricsPanel);
        }

        private void ParseSyncedLyrics(string syncedLyrics)
        {
            var regex = new Regex(@"\[(\d{2}):(\d{2}\.\d{2,3})\](.*)");
            var matches = regex.Matches(syncedLyrics);

            foreach (Match match in matches)
            {
                try
                {
                    int minutes = int.Parse(match.Groups[1].Value);
                    decimal seconds = decimal.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                    string text = match.Groups[3].Value.Trim();

                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        var time = TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds((double)seconds);
                        _lyrics.Add(new LyricLine { Time = time, Text = text });
                    }
                }
                catch (FormatException ex)
                {
                    Console.WriteLine($"Error parsing lyric timestamp: {match.Value}. Exception: {ex.Message}");
                }
            }
            _lyrics.Sort((a, b) => a.Time.CompareTo(b.Time));
            Console.WriteLine($"Successfully parsed {_lyrics.Count} synced lyric lines.");
        }

        private void ResetLyricsUI()
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                CompositionTarget.Rendering -= SmoothScrollHandler;
                LyricsPanel.Children.Clear();
                LyricsScrollViewer.ScrollToVerticalOffset(0);
                _currentLyricIndex = -1;
                _lyrics.Clear();
            });
        }

        private void SyncLyricsWithPlayback(TimeSpan playbackTime)
        {
            if (_lyrics.Count == 0) return;

            int newLyricIndex = -1;
            for (int i = _lyrics.Count - 1; i >= 0; i--)
            {
                if (_lyrics[i].Time <= playbackTime)
                {
                    newLyricIndex = i;
                    break;
                }
            }

            if (newLyricIndex != _currentLyricIndex)
            {
                _currentLyricIndex = newLyricIndex;
                Application.Current.Dispatcher.Invoke(() =>
                {
                    UpdateLyricsHighlighting(_currentLyricIndex);
                    ScrollToCurrentLyric(_currentLyricIndex);
                });
            }
        }

        private void PopulateLyricsPanel()
        {
            LyricsPanel.Children.Clear();

            if (_lyrics.Count == 0)
            {
                ShowNoLyricsMessage();
                return;
            }

            double maxWidth = Math.Max(200, LyricsScrollViewer.ActualWidth - 40);

            foreach (var lyric in _lyrics)
            {
                var tb = new TextBlock
                {
                    Text = lyric.Text,
                    FontSize = 40,
                    Foreground = Brushes.Gray,
                    Opacity = 0.5,
                    FontFamily = new FontFamily("Segoe UI Variable"),
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    MaxWidth = maxWidth,
                    Margin = new Thickness(0, 5, 0, 5)
                };
                LyricsPanel.Children.Add(tb);
            }

            UpdateLyricsHighlighting(_currentLyricIndex);
            ScrollToCurrentLyric(_currentLyricIndex);
        }

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
                }
            }
        }

        private void ShowNoLyricsMessage()
        {
            LyricsPanel.Children.Clear();
            double maxWidth = Math.Max(200, LyricsScrollViewer.ActualWidth - 40);
            var noLyricsTextBlock = new TextBlock
            {
                Text = "Lyrics not found for this track.",
                Foreground = Brushes.Gray,
                FontSize = 30,
                FontFamily = new FontFamily("Segoe UI Variable"),
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = maxWidth,
                Margin = new Thickness(20)
            };
            LyricsPanel.Children.Add(noLyricsTextBlock);
        }

        private EventHandler SmoothScrollHandler;
        private void ScrollToCurrentLyric(int currentIndex)
        {
            if (LyricsScrollViewer == null || LyricsPanel.Children.Count == 0 || currentIndex < 0 || currentIndex >= LyricsPanel.Children.Count)
            {
                if (currentIndex == -1)
                {
                    Application.Current.Dispatcher.InvokeAsync(() => LyricsScrollViewer.ScrollToVerticalOffset(0), DispatcherPriority.Background);
                }
                return;
            }

            var currentElement = LyricsPanel.Children[currentIndex] as FrameworkElement;
            if (currentElement == null) return;

            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (!currentElement.IsLoaded) return;

                try
                {
                    double currentOffset = LyricsScrollViewer.VerticalOffset;
                    double viewportHeight = LyricsScrollViewer.ViewportHeight;
                    GeneralTransform transform = currentElement.TransformToAncestor(LyricsPanel);
                    Point elementTopInPanel = transform.Transform(new Point(0, 0));
                    double targetOffset = elementTopInPanel.Y - (viewportHeight / 2.0) + (currentElement.ActualHeight / 2.0);
                    targetOffset = Math.Max(0, Math.Min(targetOffset, LyricsScrollViewer.ScrollableHeight));

                    CompositionTarget.Rendering -= SmoothScrollHandler;

                    double startOffset = currentOffset;
                    double distance = targetOffset - startOffset;
                    double duration = 0.3;
                    double startTime = -1;

                    SmoothScrollHandler = (s, e) =>
                    {
                        if (startTime < 0) startTime = (e as RenderingEventArgs)?.RenderingTime.TotalSeconds ?? 0;
                        double elapsed = ((e as RenderingEventArgs)?.RenderingTime.TotalSeconds ?? startTime) - startTime;
                        double progress = Math.Min(1.0, elapsed / duration);
                        double easeProgress = 1.0 - Math.Pow(1.0 - progress, 2);
                        double newOffset = startOffset + distance * easeProgress;

                        LyricsScrollViewer.ScrollToVerticalOffset(newOffset);

                        if (progress >= 1.0 || Math.Abs(targetOffset - newOffset) < 1.0)
                        {
                            LyricsScrollViewer.ScrollToVerticalOffset(targetOffset);
                            CompositionTarget.Rendering -= SmoothScrollHandler;
                            SmoothScrollHandler = null;
                        }
                    };
                    CompositionTarget.Rendering += SmoothScrollHandler;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Scroll error: {ex.Message}");
                }
            }, DispatcherPriority.Background);
        }

        private class LyricLine
        {
            public TimeSpan Time { get; set; }
            public string Text { get; set; }
        }

        #endregion

        #region Banners and Formatting

        private void ShowNextSongBanner()
        {
            NextSongBanner.Visibility = Visibility.Visible;
            NextSongProgressBar.Value = 0;
            var slideIn = new DoubleAnimation(100, 0, TimeSpan.FromMilliseconds(400)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(400));
            var translateTransform = new TranslateTransform();
            NextSongBanner.RenderTransform = translateTransform;
            translateTransform.BeginAnimation(TranslateTransform.XProperty, slideIn);
            NextSongBanner.BeginAnimation(OpacityProperty, fadeIn);
        }

        private void HideNextSongBanner()
        {
            var fadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(300));
            fadeOut.Completed += (s, e) =>
            {
                NextSongBanner.Visibility = Visibility.Collapsed;
                NextSongProgressBar.Value = 0;
            };
            NextSongBanner.BeginAnimation(OpacityProperty, fadeOut);
        }

        private string FormatTime(double milliseconds)
        {
            var timeSpan = TimeSpan.FromMilliseconds(milliseconds);
            return $"{(int)timeSpan.TotalMinutes}:{timeSpan.Seconds:D2}";
        }

        #endregion

        #region Windows Hook and Window Controls

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
            using (var curProcess = Process.GetCurrentProcess())
            using (var curModule = curProcess.MainModule)
            {
                return SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(curModule.ModuleName), 0);
            }
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && wParam == (IntPtr)WM_KEYDOWN)
            {
                int vkCode = Marshal.ReadInt32(lParam);
                bool winKeyPressed = (GetAsyncKeyState(0x5B) & 0x8000) != 0 || (GetAsyncKeyState(0x5C) & 0x8000) != 0;

                if (winKeyPressed && (vkCode >= 0x25 && vkCode <= 0x28)) // Arrow keys
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        if (vkCode == 0x25) MoveWindowToPreviousMonitor();
                        else if (vkCode == 0x27) MoveWindowToNextMonitor();
                    });
                    return (IntPtr)1; // Block key
                }
            }
            return CallNextHookEx(_hookID, nCode, wParam, lParam);
        }

        private void Window_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            var fadeIn = new DoubleAnimation(1, TimeSpan.FromMilliseconds(200));
            WindowControlsPanel.Visibility = Visibility.Visible;
            WindowControlsPanel.BeginAnimation(OpacityProperty, fadeIn);
        }

        private void Window_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            var fadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(200));
            fadeOut.Completed += (s, args) => WindowControlsPanel.Visibility = Visibility.Collapsed;
            WindowControlsPanel.BeginAnimation(OpacityProperty, fadeOut);
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();
        private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void MoveToNextMonitor_Click(object sender, RoutedEventArgs e) => MoveWindowToNextMonitor();
        private void MoveToPreviousMonitor_Click(object sender, RoutedEventArgs e) => MoveWindowToPreviousMonitor();

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
            WindowState = WindowState.Normal;
            Left = screen.Bounds.Left;
            Top = screen.Bounds.Top;
            Width = screen.Bounds.Width;
            Height = screen.Bounds.Height;
            WindowState = WindowState.Maximized;
        }

        #endregion
    }
}