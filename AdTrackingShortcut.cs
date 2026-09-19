internal static class AdTrackingShortcut
{
    internal const string Script = """
        (() => {
          if (globalThis.__plutoAdTrackingShortcutInstalled) return;
          globalThis.__plutoAdTrackingShortcutInstalled = true;
          globalThis.addEventListener('keydown', event => {
            if (event.repeat || event.ctrlKey || event.altKey || event.metaKey) return;
            if (event.code === 'KeyP') {
              event.preventDefault();
              globalThis.requestAdTrackingToggle();
            } else if (event.code === 'KeyN' && globalThis.top !== globalThis) {
              event.preventDefault();
              globalThis.top.postMessage('pluto-ad-detector:skip-youtube-video', '*');
            } else if ((event.code === 'KeyH' || event.key === '?') && globalThis.top !== globalThis) {
              event.preventDefault();
              globalThis.top.postMessage('pluto-ad-detector:show-youtube-help', '*');
            } else if (event.code === 'KeyR' && globalThis.top !== globalThis) {
              event.preventDefault();
              globalThis.top.postMessage('pluto-ad-detector:reload-youtube-queue', '*');
            }
          }, true);
        })();
        """;
}
