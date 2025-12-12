using System;
using System.Collections.Generic;

namespace SpottyScreen.Classes
{
    public class TrackInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public List<string> Artists { get; set; }
        public string AlbumName { get; set; }
        public int DurationMs { get; set; }
        public string ImageUrl { get; set; }
    }

    public class PlaybackState
    {
        public TrackInfo Track { get; set; }
        public int? ProgressMs { get; set; }
        public bool IsPlaying { get; set; }
    }

    public interface IMusicApiManager
    {
        event Action Authenticated;
        event Action AuthenticationStarted;
        event Action<bool, string> AuthenticationFinished;
        event Action<TrackInfo> TrackChanged;
        event Action<PlaybackState> PlaybackUpdated;
        event Action PlaybackStopped;
        event Action<TrackInfo> NextTrackAvailable;
        event Action ReauthenticationNeeded;
        event Action PollingStopped;

        void Authenticate();
        void StartPolling();
        void StopPolling();
    }
}
