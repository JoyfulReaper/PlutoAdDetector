internal static class YoutubeQueueRestoreAccess
{
    internal static bool CanReload(bool trackingPaused, bool youtubeForegrounded) =>
        trackingPaused || youtubeForegrounded;
}
