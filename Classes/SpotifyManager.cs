using SpotifyAPI.Web;
using SpottyScreen.Classes;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace SpottyScreen.Classes
{
    internal class SpotifyManager : IMusicApiManager
    {
        private SpotifyClient _spotify;
        private bool _isPolling;
        private string _currentTrackId;

        private const string ClientId = "41033dc65baf42e287b21398aafb4501";
        private const string RedirectUri = "http://127.0.0.1:5000/callback";

        public event Action Authenticated;
        public event Action AuthenticationStarted;
        public event Action<bool, string> AuthenticationFinished;
        public event Action<TrackInfo> TrackChanged;
        public event Action<PlaybackState> PlaybackUpdated;
        public event Action PlaybackStopped;
        public event Action<TrackInfo> NextTrackAvailable;
        public event Action ReauthenticationNeeded;
        public event Action PollingStopped;

        public void Authenticate()
        {
            _ = AuthenticateAsync();
        }

        public void StartPolling()
        {
            if (_isPolling) return;
            _isPolling = true;
            _ = PollPlaybackAsync();
        }

        public void StopPolling()
        {
            _isPolling = false;
        }

        private async Task AuthenticateAsync()
        {
            var savedAccessToken = Properties.Settings.Default.SpotifyAccessToken;
            var savedRefreshToken = Properties.Settings.Default.SpotifyRefreshToken;

            if (!string.IsNullOrEmpty(savedAccessToken))
            {
                _spotify = new SpotifyClient(savedAccessToken);
                try
                {
                    await _spotify.Player.GetCurrentPlayback(); // Test token
                    Authenticated?.Invoke();
                    return;
                }
                catch (APIUnauthorizedException)
                {
                    var (refreshed, _) = await RefreshAccessTokenAsync(savedRefreshToken);
                    if (refreshed)
                    {
                        Authenticated?.Invoke();
                        return;
                    }
                    // If refresh fails, ReauthenticationNeeded is fired, so we just exit.
                    return;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error initializing Spotify client: {ex.Message}");
                    // Fall through to full auth flow
                }
            }

            await StartAuthorizationCodeFlowAsync();
        }

        private async Task StartAuthorizationCodeFlowAsync()
        {
            try
            {
                AuthenticationStarted?.Invoke();
                var (verifier, challenge) = PKCEUtil.GenerateCodes();

                var loginRequest = new LoginRequest(new Uri(RedirectUri), ClientId, LoginRequest.ResponseType.Code)
                {
                    CodeChallengeMethod = "S256",
                    CodeChallenge = challenge,
                    Scope = new[] { Scopes.UserReadPlaybackState, Scopes.UserReadCurrentlyPlaying, Scopes.UserReadPlaybackPosition }
                };

                using (var http = new HttpListener())
                {
                    http.Prefixes.Add("http://127.0.0.1:5000/callback/");
                    http.Start();

                    Process.Start(new ProcessStartInfo { FileName = loginRequest.ToUri().ToString(), UseShellExecute = true });

                    var context = await http.GetContextAsync();
                    var code = context.Request.QueryString["code"];

                    const string responseHtml = "<html><head><style>body { font-family: sans-serif; background-color: #f0f0f0; text-align: center; padding-top: 50px; }</style></head><body><h1>Authentication Successful!</h1><p>You can now close this window and return to SpottyScreen.</p><script>window.close();</script></body></html>";
                    var buffer = Encoding.UTF8.GetBytes(responseHtml);
                    context.Response.ContentType = "text/html";
                    context.Response.ContentLength64 = buffer.Length;
                    await context.Response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                    context.Response.Close();
                    http.Stop();

                    if (string.IsNullOrEmpty(code))
                    {
                        AuthenticationFinished?.Invoke(false, "Authentication failed: No code received from Spotify.");
                        return;
                    }

                    var tokenRequest = new PKCETokenRequest(ClientId, code, new Uri(RedirectUri), verifier);
                    var tokenResponse = await new OAuthClient().RequestToken(tokenRequest);

                    Properties.Settings.Default.SpotifyAccessToken = tokenResponse.AccessToken;
                    Properties.Settings.Default.SpotifyRefreshToken = tokenResponse.RefreshToken;
                    Properties.Settings.Default.Save();

                    _spotify = new SpotifyClient(tokenResponse.AccessToken);
                    AuthenticationFinished?.Invoke(true, null);
                    Authenticated?.Invoke();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Authentication flow error: {ex.Message}");
                Properties.Settings.Default.SpotifyAccessToken = null;
                Properties.Settings.Default.SpotifyRefreshToken = null;
                Properties.Settings.Default.Save();
                AuthenticationFinished?.Invoke(false, $"Authentication failed: {ex.Message}");
            }
        }

        private async Task<(bool, string)> RefreshAccessTokenAsync(string refreshToken)
        {
            if (string.IsNullOrEmpty(refreshToken))
            {
                const string error = "Cannot refresh token: Refresh token is missing.";
                Console.WriteLine(error);
                ClearTokens();
                ReauthenticationNeeded?.Invoke();
                return (false, error);
            }

            try
            {
                var refreshRequest = new PKCETokenRefreshRequest(ClientId, refreshToken);
                var tokenResponse = await new OAuthClient().RequestToken(refreshRequest);

                Properties.Settings.Default.SpotifyAccessToken = tokenResponse.AccessToken;
                if (!string.IsNullOrEmpty(tokenResponse.RefreshToken))
                {
                    Properties.Settings.Default.SpotifyRefreshToken = tokenResponse.RefreshToken;
                }
                Properties.Settings.Default.Save();

                _spotify = new SpotifyClient(tokenResponse.AccessToken);
                Console.WriteLine("Access token refreshed successfully.");
                return (true, null);
            }
            catch (APIException apiEx)
            {
                var error = $"Token refresh failed: {apiEx.Message}";
                Console.WriteLine(error);
                if (apiEx.Response?.StatusCode == HttpStatusCode.BadRequest)
                {
                    ClearTokens();
                    ReauthenticationNeeded?.Invoke();
                }
                return (false, error);
            }
            catch (Exception ex)
            {
                var error = $"Unexpected error during token refresh: {ex.Message}";
                Console.WriteLine(error);
                return (false, error);
            }
        }

        private async Task PollPlaybackAsync()
        {
            Console.WriteLine("Starting playback polling...");
            while (_isPolling)
            {
                if (_spotify == null)
                {
                    Console.WriteLine("Spotify client not initialized. Stopping polling.");
                    StopPolling();
                    break;
                }

                try
                {
                    var playback = await _spotify.Player.GetCurrentlyPlaying(new PlayerCurrentlyPlayingRequest());

                    if (playback?.Item is FullTrack track && track.Id != _currentTrackId)
                    {
                        _currentTrackId = track.Id;
                        TrackChanged?.Invoke(ToTrackInfo(track));
                        await FetchNextTrackAsync();
                    }
                    else if (playback == null || playback.Item == null)
                    {
                        if (_currentTrackId != null)
                        {
                            _currentTrackId = null;
                            PlaybackStopped?.Invoke();
                        }
                    }

                    if (playback?.Item is FullTrack currentTrack)
                    {
                        var playbackState = new PlaybackState
                        {
                            Track = ToTrackInfo(currentTrack),
                            ProgressMs = playback.ProgressMs,
                            IsPlaying = playback.IsPlaying
                        };
                        PlaybackUpdated?.Invoke(playbackState);
                    }

                    await Task.Delay(200);
                }
                catch (APIUnauthorizedException)
                {
                    Console.WriteLine("Access token expired during polling, attempting refresh...");
                    var (refreshed, _) = await RefreshAccessTokenAsync(Properties.Settings.Default.SpotifyRefreshToken);
                    if (!refreshed)
                    {
                        StopPolling(); // Stop polling if refresh fails
                    }
                }
                catch (APIException apiEx)
                {
                    Console.WriteLine($"Spotify API error during polling: {apiEx.Message} (Status: {apiEx.Response?.StatusCode})");
                    await Task.Delay(apiEx.Response?.StatusCode == (HttpStatusCode)429 ? 5000 : 1000);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Unexpected error during polling: {ex.Message}");
                    await Task.Delay(2000);
                }
            }
            PollingStopped?.Invoke();
            Console.WriteLine("Polling stopped.");
        }

        private async Task FetchNextTrackAsync()
        {
            try
            {
                var queue = await _spotify.Player.GetQueue();
                if (queue?.Queue != null && queue.Queue.Any() && queue.Queue.First() is FullTrack track)
                {
                    NextTrackAvailable?.Invoke(ToTrackInfo(track));
                }
                else
                {
                    NextTrackAvailable?.Invoke(null);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error fetching next track: {ex.Message}");
                NextTrackAvailable?.Invoke(null);
            }
        }

        private void ClearTokens()
        {
            Properties.Settings.Default.SpotifyAccessToken = null;
            Properties.Settings.Default.SpotifyRefreshToken = null;
            Properties.Settings.Default.Save();
        }

        private TrackInfo ToTrackInfo(FullTrack track)
        {
            return new TrackInfo
            {
                Id = track.Id,
                Name = track.Name,
                Artists = track.Artists.Select(a => a.Name).ToList(),
                AlbumName = track.Album.Name,
                DurationMs = track.DurationMs,
                ImageUrl = track.Album.Images.FirstOrDefault()?.Url
            };
        }
    }
}
