using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace SpottyScreen.Classes
{
    internal class LocalMusicManager : IMusicApiManager, IDisposable
    {
        private GlobalSystemMediaTransportControlsSessionManager _sessionManager;
        private GlobalSystemMediaTransportControlsSession _currentSession;
        private string _currentTrackId;
        private string _currentAlbumArtPath;

        public event Action Authenticated;
        public event Action AuthenticationStarted;
        public event Action<bool, string> AuthenticationFinished;
        public event Action<TrackInfo> TrackChanged;
        public event Action<PlaybackState> PlaybackUpdated;
        public event Action PlaybackStopped;
        public event Action<TrackInfo> NextTrackAvailable;
        public event Action ReauthenticationNeeded;
        public event Action PollingStopped; // This event is kept for API compatibility.

        public void Authenticate()
        {
            _ = InitializeAsync();
        }

        public void StartPolling()
        {
            // The new implementation is event-driven and does not require polling.
            // This method is kept for API compatibility.
        }

        public void StopPolling()
        {
            // The new implementation is event-driven and does not require polling.
            // This method is kept for API compatibility.
            PollingStopped?.Invoke();
        }

        private async Task InitializeAsync()
        {
            AuthenticationStarted?.Invoke();
            try
            {
                _sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                _sessionManager.CurrentSessionChanged += OnCurrentSessionChanged;

                // Process the initial session.
                OnCurrentSessionChanged(_sessionManager, null);

                AuthenticationFinished?.Invoke(true, null);
                Authenticated?.Invoke();
            }
            catch (Exception ex)
            {
                AuthenticationFinished?.Invoke(false, $"Failed to access Windows Media Transport Controls: {ex.Message}");
            }
        }

        private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
        {
            if (_currentSession != null)
            {
                _currentSession.MediaPropertiesChanged -= OnMediaPropertiesChanged;
                _currentSession.PlaybackInfoChanged -= OnPlaybackInfoChanged;
                _currentSession.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
                _currentSession = null;
            }

            _currentSession = sender.GetCurrentSession();

            if (_currentSession != null)
            {
                _currentSession.MediaPropertiesChanged += OnMediaPropertiesChanged;
                _currentSession.PlaybackInfoChanged += OnPlaybackInfoChanged;
                _currentSession.TimelinePropertiesChanged += OnTimelinePropertiesChanged;

                // Update with the new session's data.
                _ = UpdateMediaPropertiesAsync();
                _ = UpdatePlaybackStateAsync();
            }
            else
            {
                // No active media session.
                ResetCurrentTrack();
                PlaybackStopped?.Invoke();
            }
        }

        private async void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
        {
            await UpdateMediaPropertiesAsync();
        }

        private async void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
        {
            await UpdatePlaybackStateAsync();
        }

        private async void OnTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
        {
            await UpdatePlaybackStateAsync();
        }

        private async Task UpdateMediaPropertiesAsync()
        {
            if (_currentSession == null) return;

            try
            {
                var mediaProperties = await _currentSession.TryGetMediaPropertiesAsync();
                if (mediaProperties == null) return;

                var trackId = $"{mediaProperties.Title}_{mediaProperties.Artist}";
                if (trackId == _currentTrackId) return;

                _currentTrackId = trackId;

                var timelineProperties = _currentSession.GetTimelineProperties();
                var albumArtPath = await GetAlbumArtPathAsync(mediaProperties.Thumbnail);

                var trackInfo = new TrackInfo
                {
                    Id = _currentTrackId,
                    Name = mediaProperties.Title ?? "Unknown Title",
                    Artists = new List<string> { mediaProperties.Artist ?? "Unknown Artist" },
                    AlbumName = mediaProperties.AlbumTitle ?? "Unknown Album",
                    DurationMs = (int)(timelineProperties?.EndTime.TotalMilliseconds ?? 0),
                    ImageUrl = albumArtPath
                };

                TrackChanged?.Invoke(trackInfo);
                _currentAlbumArtPath = albumArtPath;
            }
            catch (Exception ex)
            {
                // Log error
            }
        }

        private async Task UpdatePlaybackStateAsync()
        {
            if (_currentSession == null) return;

            try
            {
                var playbackInfo = _currentSession.GetPlaybackInfo();
                if (playbackInfo == null) return;

                var mediaProperties = await _currentSession.TryGetMediaPropertiesAsync();
                if (mediaProperties == null) return;

                var timelineProperties = _currentSession.GetTimelineProperties();

                var trackInfo = new TrackInfo
                {
                    Id = _currentTrackId,
                    Name = mediaProperties.Title ?? "Unknown Title",
                    Artists = new List<string> { mediaProperties.Artist ?? "Unknown Artist" },
                    AlbumName = mediaProperties.AlbumTitle ?? "Unknown Album",
                    DurationMs = (int)(timelineProperties?.EndTime.TotalMilliseconds ?? 0),
                    ImageUrl = _currentAlbumArtPath
                };

                var playbackState = new PlaybackState
                {
                    Track = trackInfo,
                    ProgressMs = (int)(timelineProperties?.Position.TotalMilliseconds ?? 0),
                    IsPlaying = playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                };

                PlaybackUpdated?.Invoke(playbackState);
            }
            catch (Exception ex)
            {
                // Log error
            }
        }

        private async Task<string> GetAlbumArtPathAsync(IRandomAccessStreamReference thumbnail)
        {
            if (thumbnail == null) return null;

            try
            {
                using (var stream = await thumbnail.OpenReadAsync())
                {
                    var tempPath = Path.Combine(Path.GetTempPath(), $"spottyscreen_{Guid.NewGuid()}.jpg");
                    using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
                    {
                        await stream.AsStreamForRead().CopyToAsync(fileStream);
                    }
                    return tempPath;
                }
            }
            catch (Exception ex)
            {
                // Log error
                return null;
            }
        }

        private void ResetCurrentTrack()
        {
            _currentTrackId = null;
            if (_currentAlbumArtPath != null && File.Exists(_currentAlbumArtPath))
            {
                try
                {
                    File.Delete(_currentAlbumArtPath);
                }
                catch (Exception) { /* Suppress delete errors */ }
                _currentAlbumArtPath = null;
            }
        }

        public void Dispose()
        {
            if (_sessionManager != null)
            {
                _sessionManager.CurrentSessionChanged -= OnCurrentSessionChanged;
            }

            if (_currentSession != null)
            {
                _currentSession.MediaPropertiesChanged -= OnMediaPropertiesChanged;
                _currentSession.PlaybackInfoChanged -= OnPlaybackInfoChanged;
                _currentSession.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
            }

            ResetCurrentTrack();
        }
    }
}
