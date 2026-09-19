internal static class AdTrackingShortcut
{
    internal const string Script = """
        (() => {
          if (globalThis.__plutoAdTrackingShortcutInstalled) return;
          globalThis.__plutoAdTrackingShortcutInstalled = true;
          globalThis.addEventListener('keydown', event => {
            if (event.code !== 'KeyP' || event.repeat || event.ctrlKey || event.altKey || event.metaKey) return;
            event.preventDefault();
            globalThis.requestAdTrackingToggle();
          }, true);
        })();
        """;
}
