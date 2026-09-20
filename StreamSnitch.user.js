// ==UserScript==
// @name         Streaming Ad Signal Probe
// @namespace    https://kgivler.com/
// @version      0.1.0
// @description  Generic probe for DOM, accessibility, player, network, and JS state changes around streaming ad breaks.
// @match        http://*/*
// @match        https://*/*
// @run-at       document-start
// @grant        none
// ==/UserScript==

(() => {
    'use strict';

    const PREFIX = '[AD-PROBE]';
    const MAX_EVENTS = 3000;
    const MAX_TEXT = 500;

    const interestingWords =
        /\b(ad|ads|advert|advertisement|commercial|sponsor|sponsored|break|preroll|pre-roll|midroll|mid-roll|postroll|post-roll|vast|vmap|ima|ssai|csai|cue|beacon|quartile|tracking)\b/i;

    const interestingIdentity =
        /(ad[-_]?|[-_]?ad\b|advert|commercial|sponsor|preroll|midroll|postroll|vast|vmap|ima|ssai|player|video)/i;

    const interestingRoles =
        /^(status|timer|alert|alertdialog|progressbar)$/i;

    const timerText =
        /^\s*(?:\d{1,2}:)?\d{1,2}:\d{2}\s*$/;

    const manifestUrl =
        /\.(?:m3u8|mpd)(?:$|[?#])|manifest/i;

    const events = [];
    const shadowRoots = new Set();
    const videoIds = new WeakMap();
    const observedTracks = new WeakSet();
    const elementSignatures = new WeakMap();

    let nextVideoId = 1;
    let paused = false;
    let startedAt = performance.now();

    function now() {
        return new Date().toISOString();
    }

    function elapsed() {
        return Math.round(performance.now() - startedAt);
    }

    function trim(value, max = MAX_TEXT) {
        if (value == null)
            return null;

        value = String(value).replace(/\s+/g, ' ').trim();

        return value.length > max
            ? `${value.slice(0, max)}…`
            : value;
    }

    function record(kind, details, important = false) {
        if (paused && !important)
            return;

        const entry = {
            time: now(),
            elapsedMs: elapsed(),
            kind,
            details
        };

        events.push(entry);

        if (events.length > MAX_EVENTS)
            events.splice(0, events.length - MAX_EVENTS);

        console.log(`${PREFIX} ${kind}`, details);
    }

    function safeAttribute(element, name) {
        try {
            return element.getAttribute(name);
        } catch {
            return null;
        }
    }

    function pseudoContent(element, pseudo) {
        try {
            const value = getComputedStyle(element, pseudo).content;

            if (!value || value === 'none' || value === 'normal')
                return null;

            return trim(value.replace(/^["']|["']$/g, ''));
        } catch {
            return null;
        }
    }

    function bounds(element) {
        try {
            const rect = element.getBoundingClientRect();

            return {
                x: Math.round(rect.x),
                y: Math.round(rect.y),
                width: Math.round(rect.width),
                height: Math.round(rect.height)
            };
        } catch {
            return null;
        }
    }

    function visible(element) {
        try {
            const rect = element.getBoundingClientRect();

            if (rect.width < 1 || rect.height < 1)
                return false;

            const style = getComputedStyle(element);

            return style.display !== 'none' &&
                style.visibility !== 'hidden' &&
                Number(style.opacity || 1) > 0;
        } catch {
            return false;
        }
    }

    function describeElement(element) {
        if (!(element instanceof Element))
            return null;

        const text = trim(element.innerText);
        const rawText = trim(element.textContent);

        const info = {
            tag: element.tagName,
            id: trim(element.id, 200),
            class: trim(
                typeof element.className === 'string'
                    ? element.className
                    : element.getAttribute('class'),
                300),
            role: safeAttribute(element, 'role'),
            ariaLabel: safeAttribute(element, 'aria-label'),
            ariaLive: safeAttribute(element, 'aria-live'),
            ariaHidden: safeAttribute(element, 'aria-hidden'),
            title: safeAttribute(element, 'title'),
            testId: safeAttribute(element, 'data-testid'),
            dataAd: safeAttribute(element, 'data-ad'),
            text,
            rawText,
            before: pseudoContent(element, '::before'),
            after: pseudoContent(element, '::after'),
            visible: visible(element),
            bounds: bounds(element)
        };

        return info;
    }

    function searchable(info) {
        return [
            info.id,
            info.class,
            info.role,
            info.ariaLabel,
            info.ariaLive,
            info.title,
            info.testId,
            info.dataAd,
            info.text,
            info.rawText,
            info.before,
            info.after
        ].filter(Boolean).join(' ');
    }

    function looksInteresting(info) {
        const combined = searchable(info);

        if (interestingWords.test(combined))
            return true;

        if (interestingRoles.test(info.role || ''))
            return true;

        if (info.ariaLive)
            return true;

        if (interestingIdentity.test([
            info.id,
            info.class,
            info.testId
        ].filter(Boolean).join(' ')))
            return true;

        return timerText.test(info.text || '') ||
            timerText.test(info.rawText || '') ||
            timerText.test(info.before || '') ||
            timerText.test(info.after || '');
    }

    function describeAncestors(element, depth = 5) {
        const result = [];

        let current = element?.parentElement;

        for (let i = 0; current && i < depth; i++, current = current.parentElement) {
            result.push({
                tag: current.tagName,
                id: trim(current.id, 120),
                class: trim(
                    typeof current.className === 'string'
                        ? current.className
                        : current.getAttribute('class'),
                    200),
                role: safeAttribute(current, 'role'),
                ariaLabel: safeAttribute(current, 'aria-label'),
                testId: safeAttribute(current, 'data-testid')
            });
        }

        return result;
    }

    function inspectElement(element, reason) {
        if (!(element instanceof Element))
            return;

        const info = describeElement(element);

        if (!info || !looksInteresting(info))
            return;

        const signature = JSON.stringify(info);

        if (elementSignatures.get(element) === signature)
            return;

        elementSignatures.set(element, signature);

        record('DOM', {
            reason,
            element: info,
            ancestors: describeAncestors(element)
        });
    }

    function scanTree(root, reason, limit = 300) {
        if (!root)
            return;

        if (root instanceof Element)
            inspectElement(root, reason);

        let elements;

        try {
            elements = root.querySelectorAll?.('*');
        } catch {
            return;
        }

        if (!elements)
            return;

        const count = Math.min(elements.length, limit);

        for (let i = 0; i < count; i++)
            inspectElement(elements[i], reason);

        if (elements.length > limit) {
            record('SCAN-LIMIT', {
                reason,
                total: elements.length,
                inspected: limit
            });
        }
    }

    function createObserver(label) {
        return new MutationObserver(mutations => {
            for (const mutation of mutations) {
                if (mutation.type === 'attributes') {
                    inspectElement(
                        mutation.target,
                        `${label}:attribute:${mutation.attributeName}`);
                }

                if (mutation.type === 'characterData') {
                    inspectElement(
                        mutation.target.parentElement,
                        `${label}:text`);
                }

                for (const node of mutation.addedNodes) {
                    if (node instanceof Element)
                        scanTree(node, `${label}:added`, 150);
                }
            }
        });
    }

    function observeRoot(root, label) {
        try {
            const observer = createObserver(label);

            observer.observe(root, {
                subtree: true,
                childList: true,
                attributes: true,
                characterData: true
            });

            scanTree(root, `${label}:initial`);
        } catch (error) {
            record('OBSERVER-ERROR', {
                label,
                error: String(error)
            });
        }
    }

    /*
     * Capture shadow roots as they're created.
     *
     * Even a closed ShadowRoot is returned to attachShadow()'s caller, so if this
     * hook runs early enough we can retain our own reference without changing
     * open/closed behavior.
     */
    const originalAttachShadow = Element.prototype.attachShadow;

    Element.prototype.attachShadow = function (options) {
        const root = originalAttachShadow.call(this, options);

        shadowRoots.add(root);

        record('SHADOW-ROOT', {
            mode: options?.mode,
            host: describeElement(this)
        });

        observeRoot(root, `shadow:${options?.mode || 'unknown'}`);

        return root;
    };

    function videoId(video) {
        let id = videoIds.get(video);

        if (!id) {
            id = nextVideoId++;
            videoIds.set(video, id);
        }

        return id;
    }

    function describeVideo(video) {
        return {
            id: videoId(video),
            currentSrc: trim(video.currentSrc, 1000),
            currentTime: Number(video.currentTime.toFixed(2)),
            duration: Number.isFinite(video.duration)
                ? Number(video.duration.toFixed(2))
                : String(video.duration),
            paused: video.paused,
            ended: video.ended,
            muted: video.muted,
            volume: video.volume,
            playbackRate: video.playbackRate,
            readyState: video.readyState,
            networkState: video.networkState,
            videoWidth: video.videoWidth,
            videoHeight: video.videoHeight,
            autoplay: video.autoplay,
            loop: video.loop,
            controls: video.controls
        };
    }

    const previousVideoStates = new WeakMap();

    function meaningfulVideoState(state) {
        return {
            currentSrc: state.currentSrc,
            duration: state.duration,
            paused: state.paused,
            ended: state.ended,
            muted: state.muted,
            playbackRate: state.playbackRate,
            readyState: state.readyState,
            networkState: state.networkState,
            videoWidth: state.videoWidth,
            videoHeight: state.videoHeight
        };
    }

    function inspectTracks(video) {
        for (const track of video.textTracks || []) {
            if (observedTracks.has(track))
                continue;

            observedTracks.add(track);

            record('TEXT-TRACK', {
                videoId: videoId(video),
                kind: track.kind,
                label: track.label,
                language: track.language,
                mode: track.mode
            });

            track.addEventListener('cuechange', () => {
                const cues = Array.from(track.activeCues || []).map(cue => ({
                    id: cue.id,
                    startTime: cue.startTime,
                    endTime: cue.endTime,
                    text: trim(cue.text ?? cue.value ?? String(cue), 1000)
                }));

                record('CUE', {
                    videoId: videoId(video),
                    kind: track.kind,
                    label: track.label,
                    cues
                });
            });
        }
    }

    function pollVideos() {
        if (!document.querySelectorAll)
            return;

        for (const video of document.querySelectorAll('video')) {
            const state = describeVideo(video);
            const meaningful = meaningfulVideoState(state);
            const previous = previousVideoStates.get(video);

            if (!previous ||
                JSON.stringify(previous) !== JSON.stringify(meaningful)) {

                record('VIDEO', state);

                previousVideoStates.set(video, meaningful);
            }

            inspectTracks(video);
        }
    }

    function interestingUrl(url) {
        url = String(url || '');

        return interestingWords.test(url) ||
            manifestUrl.test(url);
    }

    /*
     * fetch()
     */
    const originalFetch = window.fetch;

    if (typeof originalFetch === 'function') {
        window.fetch = async function (...args) {
            const request = args[0];
            const url = request?.url || request;

            if (interestingUrl(url)) {
                record('FETCH', {
                    method: args[1]?.method || request?.method || 'GET',
                    url: trim(url, 2000)
                });
            }

            const response = await originalFetch.apply(this, args);

            if (interestingUrl(url) || interestingUrl(response.url)) {
                record('FETCH-RESPONSE', {
                    status: response.status,
                    url: trim(response.url, 2000),
                    contentType: response.headers.get('content-type')
                });
            }

            return response;
        };
    }

    /*
     * XMLHttpRequest
     */
    const originalXhrOpen = XMLHttpRequest.prototype.open;
    const originalXhrSend = XMLHttpRequest.prototype.send;

    XMLHttpRequest.prototype.open = function (method, url, ...rest) {
        this.__adProbeMethod = method;
        this.__adProbeUrl = String(url);

        return originalXhrOpen.call(this, method, url, ...rest);
    };

    XMLHttpRequest.prototype.send = function (...args) {
        const url = this.__adProbeUrl;

        if (interestingUrl(url)) {
            record('XHR', {
                method: this.__adProbeMethod,
                url: trim(url, 2000)
            });

            this.addEventListener('loadend', () => {
                record('XHR-RESPONSE', {
                    status: this.status,
                    url: trim(this.responseURL || url, 2000),
                    contentType: this.getResponseHeader('content-type')
                });
            }, { once: true });
        }

        return originalXhrSend.apply(this, args);
    };

    /*
     * navigator.sendBeacon()
     *
     * Ad/tracking systems love this thing.
     */
    const originalSendBeacon = navigator.sendBeacon?.bind(navigator);

    if (originalSendBeacon) {
        navigator.sendBeacon = function (url, data) {
            if (interestingUrl(url)) {
                record('BEACON', {
                    url: trim(url, 2000),
                    dataType: data?.constructor?.name ?? null
                });
            }

            return originalSendBeacon(url, data);
        };
    }

    /*
     * Resource Timing sees requests we didn't necessarily initiate through
     * fetch/XHR in this JavaScript world.
     */
    try {
        const performanceObserver = new PerformanceObserver(list => {
            for (const entry of list.getEntries()) {
                if (interestingUrl(entry.name)) {
                    record('RESOURCE', {
                        initiatorType: entry.initiatorType,
                        url: trim(entry.name, 2000),
                        durationMs: Math.round(entry.duration)
                    });
                }
            }
        });

        performanceObserver.observe({
            type: 'resource',
            buffered: true
        });
    } catch (error) {
        record('PERFORMANCE-OBSERVER-ERROR', String(error));
    }

    /*
     * Look at suspicious window globals without invoking getters.
     */
    function inspectGlobals() {
        const descriptors = Object.getOwnPropertyDescriptors(window);
        const result = [];

        for (const [name, descriptor] of Object.entries(descriptors)) {
            if (!interestingIdentity.test(name))
                continue;

            if (!('value' in descriptor))
                continue;

            const value = descriptor.value;

            if (value == null ||
                ['string', 'number', 'boolean'].includes(typeof value)) {
                result.push({
                    name,
                    value: trim(value, 500)
                });

                continue;
            }

            if (typeof value === 'function') {
                result.push({
                    name,
                    type: 'function',
                    functionName: value.name
                });

                continue;
            }

            if (typeof value === 'object') {
                let keys = [];

                try {
                    keys = Object.keys(value).slice(0, 50);
                } catch {
                    // Proxy or hostile object. That's fine.
                }

                result.push({
                    name,
                    type: value.constructor?.name || 'object',
                    keys
                });
            }
        }

        return result;
    }

    /*
     * Framework hints on the video element and its ancestors. This doesn't try
     * to crawl entire React/Vue/Angular state trees because that gets insane
     * very quickly.
     */
    function frameworkHints(element) {
        const hints = [];

        for (let current = element, depth = 0;
             current && depth < 8;
             current = current.parentElement, depth++) {

            let names;

            try {
                names = Object.getOwnPropertyNames(current);
            } catch {
                continue;
            }

            const interesting = names.filter(name =>
                /^__react/i.test(name) ||
                /^__vue/i.test(name) ||
                /^__ng/i.test(name) ||
                interestingIdentity.test(name));

            if (interesting.length) {
                hints.push({
                    depth,
                    element: {
                        tag: current.tagName,
                        id: current.id,
                        class: trim(current.className, 200)
                    },
                    properties: interesting.slice(0, 30)
                });
            }
        }

        return hints;
    }

    function snapshot() {
        const videos = Array.from(document.querySelectorAll('video'));

        const report = {
            generatedAt: now(),
            url: location.href,
            title: document.title,
            videos: videos.map(video => ({
                state: describeVideo(video),
                bounds: bounds(video),
                frameworkHints: frameworkHints(video),
                textTracks: Array.from(video.textTracks || []).map(track => ({
                    kind: track.kind,
                    label: track.label,
                    language: track.language,
                    mode: track.mode
                }))
            })),
            interestingElements: [],
            globals: inspectGlobals(),
            shadowRootCount: shadowRoots.size,
            recentEvents: events.slice(-500)
        };

        for (const element of document.querySelectorAll('*')) {
            const info = describeElement(element);

            if (info && looksInteresting(info)) {
                report.interestingElements.push({
                    element: info,
                    ancestors: describeAncestors(element)
                });

                if (report.interestingElements.length >= 200)
                    break;
            }
        }

        console.log(`${PREFIX} SNAPSHOT`, report);

        return report;
    }

    function saveReport() {
        const report = snapshot();

        report.events = events;

        const blob = new Blob(
            [JSON.stringify(report, null, 2)],
            { type: 'application/json' });

        const url = URL.createObjectURL(blob);
        const link = document.createElement('a');

        link.href = url;
        link.download =
            `ad-probe-${location.hostname}-${new Date()
                .toISOString()
                .replace(/[:.]/g, '-')}.json`;

        document.documentElement.appendChild(link);
        link.click();
        link.remove();

        setTimeout(() => URL.revokeObjectURL(url), 1000);

        record('REPORT-SAVED', {
            events: events.length
        }, true);
    }

    /*
     * Public console API.
     */
    window.__adProbe = {
        events,
        snapshot,
        save: saveReport,

        pause() {
            paused = true;
            record('PROBE-PAUSED', {}, true);
        },

        resume() {
            paused = false;
            record('PROBE-RESUMED', {}, true);
        },

        clear() {
            events.length = 0;
            startedAt = performance.now();
            console.clear();
            record('PROBE-CLEARED', {}, true);
        }
    };

    /*
     * Hotkeys:
     *
     * Alt+Shift+D  snapshot
     * Alt+Shift+S  save JSON report
     * Alt+Shift+P  pause/resume logging
     * Alt+Shift+C  clear event history
     */
    window.addEventListener('keydown', event => {
        if (!event.altKey || !event.shiftKey)
            return;

        switch (event.code) {
            case 'KeyD':
                event.preventDefault();
                snapshot();
                break;

            case 'KeyS':
                event.preventDefault();
                saveReport();
                break;

            case 'KeyP':
                event.preventDefault();

                paused = !paused;

                record(
                    paused ? 'PROBE-PAUSED' : 'PROBE-RESUMED',
                    {},
                    true);

                break;

            case 'KeyC':
                event.preventDefault();
                window.__adProbe.clear();
                break;
        }
    }, true);

    function startDomObservation() {
        if (!document.documentElement) {
            requestAnimationFrame(startDomObservation);
            return;
        }

        observeRoot(document.documentElement, 'document');

        setInterval(pollVideos, 1000);

        record('STARTED', {
            host: location.hostname,
            url: location.href,
            hotkeys: {
                snapshot: 'Alt+Shift+D',
                save: 'Alt+Shift+S',
                pause: 'Alt+Shift+P',
                clear: 'Alt+Shift+C'
            }
        }, true);
    }

    startDomObservation();
})();