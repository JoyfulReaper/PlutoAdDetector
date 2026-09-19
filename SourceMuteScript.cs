internal static class SourceMuteScript
{
    internal const string Script = """
        muted => {
          const observerKey = '__plutoAdDetectorMuteObserver';
          const desiredStateKey = '__plutoAdDetectorSourceMuted';
          globalThis[desiredStateKey] = muted;

          const setMutedOnVideos = root => {
            if (root.matches?.('video')) root.muted = muted;
            for (const video of root.querySelectorAll?.('video') || []) video.muted = muted;
          };

          if (!muted) {
            globalThis[observerKey]?.disconnect();
            delete globalThis[observerKey];
            setMutedOnVideos(document);
            return;
          }

          if (!globalThis[observerKey] && typeof MutationObserver === 'function' && document.documentElement) {
            const observer = new MutationObserver(records => {
              if (!globalThis[desiredStateKey]) return;
              for (const record of records) {
                for (const node of record.addedNodes) {
                  if (node.nodeType === 1) setMutedOnVideos(node);
                }
              }
            });
            observer.observe(document.documentElement, { childList: true, subtree: true });
            globalThis[observerKey] = observer;
          }

          setMutedOnVideos(document);
        }
        """;
}
