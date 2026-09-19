internal static class PlutoDetectionScript
{
    internal const string Script = """
        mode => {
          const visible = (element, rect) => {
            if (!rect || rect.width < 1 || rect.height < 1) return false;
            const style = getComputedStyle(element);
            return style.display !== 'none' && style.visibility !== 'hidden' &&
                   Number(style.opacity || 1) > 0;
          };

          const videos = [...document.querySelectorAll('video')]
            .map(element => ({ element, rect: element.getBoundingClientRect() }))
            .filter(item => visible(item.element, item.rect));
          const player = videos.sort((a, b) =>
            (b.rect.width * b.rect.height) - (a.rect.width * a.rect.height))[0];

          if (!player) {
            return JSON.stringify({ isAd: false, method: 'DOM', hasPlayer: false, x: 0, y: 0, width: 0, height: 0 });
          }

          const p = player.rect;
          const region = {
            left: Math.max(0, p.left),
            top: Math.max(0, p.top),
            right: Math.min(innerWidth, p.left + Math.min(p.width * 0.45, 640)),
            bottom: Math.min(innerHeight, p.top + Math.min(p.height * 0.30, 260))
          };
          const intersects = rect => rect.right > region.left && rect.left < region.right &&
                                     rect.bottom > region.top && rect.top < region.bottom;
          const adWords = /\b(ad|ads|advertisement|commercial break|sponsored)\b/i;
          const adIdentity = /(^|[-_])(ad|ads|advert|advertisement)([-_]|$)|adbadge|adindicator|adcountdown/i;

          let candidates;
          if (mode === 'full') {
            // Preserve the original whole-document scan for fallback/debugging.
            candidates = document.querySelectorAll('*');
          } else {
            const candidateSet = new Set();
            const likelySelector = [
              '[aria-label*="ad" i]', '[aria-label*="commercial" i]', '[aria-label*="sponsor" i]',
              '[title*="ad" i]', '[title*="commercial" i]', '[title*="sponsor" i]',
              '[data-testid*="ad" i]', '[data-testid*="commercial" i]', '[data-testid*="sponsor" i]',
              '[id*="ad" i]', '[class*="ad" i]', '[id*="commercial" i]', '[class*="commercial" i]',
              '[id*="sponsor" i]', '[class*="sponsor" i]', '[role="status"]', '[role="timer"]',
              '[aria-live]', '[data-ad]'
            ].join(',');

            const namedContainer = player.element.closest(
              '[data-testid*="player" i], [id*="player" i], [class*="player-container" i], [class*="video-player" i], [role="application"]');
            let playerContainer = namedContainer || player.element.parentElement || player.element;
            let ancestor = player.element.parentElement;
            for (let depth = 0; ancestor && ancestor !== document.body && depth < 6; depth++, ancestor = ancestor.parentElement) {
              const rect = ancestor.getBoundingClientRect();
              const containsPlayer = rect.left <= p.left + 2 && rect.top <= p.top + 2 &&
                rect.right >= p.right - 2 && rect.bottom >= p.bottom - 2;
              if (containsPlayer && rect.width <= p.width * 1.6 && rect.height <= p.height * 1.6)
                playerContainer = ancestor;
            }

            for (const element of playerContainer.querySelectorAll?.(likelySelector) || []) candidateSet.add(element);
            // Include portal-style overlays rendered outside the player container, but
            // only when they carry a likely ad/accessibility identity.
            for (const element of document.querySelectorAll(likelySelector)) candidateSet.add(element);

            // Hit-test a bounded grid so concise visible text such as "Ad 0:30" is
            // considered even when it has no useful class or accessibility metadata.
            for (let row = 0; row < 6; row++) {
              const y = region.top + ((region.bottom - region.top) * (row + 0.5) / 6);
              for (let column = 0; column < 10; column++) {
                const x = region.left + ((region.right - region.left) * (column + 0.5) / 10);
                const hit = document.elementFromPoint(x, y);
                let element = hit;
                for (let depth = 0; element && depth < 4; depth++, element = element.parentElement) {
                  candidateSet.add(element);
                  if (element === playerContainer) break;
                }
              }
            }
            candidates = candidateSet;
          }

          let isAd = false;
          for (const element of candidates) {
            const rect = element.getBoundingClientRect();
            if (!intersects(rect) || !visible(element, rect)) continue;
            if (rect.width > (region.right - region.left) * 1.5 ||
                rect.height > (region.bottom - region.top) * 1.5) continue;

            const text = (element.innerText || element.textContent || '').trim().replace(/\s+/g, ' ');
            const aria = element.getAttribute('aria-label') || '';
            const title = element.getAttribute('title') || '';
            const role = element.getAttribute('role') || '';
            const testId = element.getAttribute('data-testid') || '';
            const identity = `${element.id} ${element.className || ''} ${testId}`;
            const conciseText = text.length <= 160 ? text : '';

            if (adWords.test(`${conciseText} ${aria} ${title}`) ||
                (adIdentity.test(identity) && !/load|download/i.test(identity)) ||
                (/status|timer/i.test(role) && adWords.test(`${aria} ${conciseText}`))) {
              isAd = true;
              break;
            }
          }

          return JSON.stringify({
            isAd,
            method: 'DOM',
            hasPlayer: true,
            x: Math.max(0, region.left),
            y: Math.max(0, region.top),
            width: Math.max(1, region.right - region.left),
            height: Math.max(1, region.bottom - region.top)
          });
        }
        """;
}
