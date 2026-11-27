/**
 * NightCodeForge Client-Side Runtime Utilities
 * Handles chat auto-scrolling, transcript tracking, and DOM observations.
 */
window.NightCodeForge = window.NightCodeForge || {};

(function () {
    const activeScrollStates = new Map();

    /**
     * Scrolls the container to the very bottom.
     * @param {string} elementId - The ID of the scrollable container.
     * @param {boolean} [force=true] - Whether to re-engage auto-scrolling if user was scrolled up.
     * @param {boolean} [smooth=false] - Whether to smoothly animate the scroll.
     */
    window.NightCodeForge.scrollToBottom = function (elementId, force = true, smooth = false) {
        const state = activeScrollStates.get(elementId);
        const el = document.getElementById(elementId) || (state ? state.el : null);
        const sc = state ? state.scrollContainer : el;
        if (!sc) return;

        if (state && force) {
            state.userScrolledUp = false;
            if (state.scrollBtn) {
                state.scrollBtn.style.display = 'none';
            }
            if (el) {
                const thinkEls = el.querySelectorAll('.think-content');
                for (let i = 0; i < thinkEls.length; i++) {
                    const t = thinkEls[i];
                    t._userScrolledUp = false;
                    if (smooth && typeof t.scrollTo === 'function') {
                        t.scrollTo({ top: t.scrollHeight, behavior: 'smooth' });
                    } else {
                        t.scrollTop = t.scrollHeight;
                    }
                }
            }
        }

        requestAnimationFrame(() => {
            if (smooth && typeof sc.scrollTo === 'function') {
                sc.scrollTo({ top: sc.scrollHeight, behavior: 'smooth' });
            } else {
                sc.scrollTop = sc.scrollHeight;
            }
        });
    };

    /**
     * Initializes smart auto-scrolling on a chat transcript or stage execution container.
     * - Keeps the viewport pinned to bottom when new streaming chunks, thinking traces, or messages arrive.
     * - Pin active mutating reasoning traces (.think-content) to bottom as deliberation tokens stream in.
     * - Pauses auto-scroll if the user scrolls up to read history (both at container level and per thinking box).
     * - Displays an optional floating 'scroll to bottom' button when user is scrolled up or when auto-scroll is off.
     * - Resumes auto-scrolling when user returns to bottom or clicks the button.
     *
     * @param {string} elementId - The ID of the container (e.g. 'chatMessagesContainer' or 'stageConversationContainer')
     * @param {string} [scrollBtnId] - Optional ID of floating scroll-to-bottom button
     * @param {object} [options] - Optional settings, e.g. { scrollContainerSelector: '.forge-main-content', autoScrollOnByDefault: false }
     */
    window.NightCodeForge.initAutoScroll = function (elementId, scrollBtnId, options) {
        const el = document.getElementById(elementId);
        if (!el) return;

        // Clean up any previous state attached to this element
        window.NightCodeForge.disposeAutoScroll(elementId);

        // Discover scroll container (either specified by selector, self if scrollable, or nearest scrollable ancestor)
        let scrollContainer = null;
        if (options && options.scrollContainerSelector) {
            scrollContainer = document.querySelector(options.scrollContainerSelector);
        }
        if (!scrollContainer) {
            try {
                let parent = el;
                while (parent && parent !== document.body && parent !== document.documentElement) {
                    const style = window.getComputedStyle ? window.getComputedStyle(parent) : null;
                    if (style && (style.overflowY === 'auto' || style.overflowY === 'scroll')) {
                        scrollContainer = parent;
                        break;
                    }
                    parent = parent.parentElement;
                }
            } catch (_) {
                // Fallback for non-browser or test environments
            }
        }
        if (!scrollContainer) {
            scrollContainer = el;
        }

        const autoScrollOnByDefault = (options && options.autoScrollOnByDefault !== undefined)
            ? Boolean(options.autoScrollOnByDefault)
            : true;

        const scrollBtn = scrollBtnId ? document.getElementById(scrollBtnId) : null;
        const state = {
            el: el,
            scrollContainer: scrollContainer,
            scrollBtnId: scrollBtnId,
            scrollBtn: scrollBtn,
            autoScrollOnByDefault: autoScrollOnByDefault,
            userScrolledUp: !autoScrollOnByDefault,
            rafPending: false,
            mutatedThinkEls: new Set(),
            observer: null,
            resizeObserver: null,
            scrollHandler: null,
            thinkScrollHandler: null,
            btnHandler: null,
            toggleHandler: null,
            updateScrollBtn: null
        };

        const threshold = 100; // px threshold from bottom to consider container "at bottom"
        const thinkThreshold = 40; // px threshold from bottom inside .think-content

        const updateScrollBtn = () => {
            if (!state.scrollBtn && state.scrollBtnId) {
                state.scrollBtn = document.getElementById(state.scrollBtnId);
                if (state.scrollBtn && !state.btnHandler) {
                    state.btnHandler = (e) => {
                        e.preventDefault();
                        window.NightCodeForge.scrollToBottom(elementId, true, true);
                    };
                    state.scrollBtn.addEventListener('click', state.btnHandler);
                }
            }
            if (!state.scrollBtn) return;
            const sc = state.scrollContainer;
            const isScrollable = sc.scrollHeight > sc.clientHeight + threshold;
            if (state.userScrolledUp && isScrollable) {
                state.scrollBtn.style.display = 'flex';
            } else {
                state.scrollBtn.style.display = 'none';
            }
        };
        state.updateScrollBtn = updateScrollBtn;

        const scheduleRafUpdate = () => {
            if (state.rafPending) return;
            state.rafPending = true;
            requestAnimationFrame(() => {
                state.rafPending = false;
                if (!state.userScrolledUp) {
                    state.scrollContainer.scrollTop = state.scrollContainer.scrollHeight;

                    state.mutatedThinkEls.forEach(thinkEl => {
                        if (!thinkEl._userScrolledUp) {
                            thinkEl.scrollTop = thinkEl.scrollHeight;
                        }
                    });
                }
                state.mutatedThinkEls.clear();
                updateScrollBtn();
            });
        };

        state.scrollHandler = () => {
            const sc = state.scrollContainer;
            const distanceFromBottom = sc.scrollHeight - sc.scrollTop - sc.clientHeight;
            state.userScrolledUp = (distanceFromBottom > threshold);
            updateScrollBtn();
        };

        state.scrollContainer.addEventListener('scroll', state.scrollHandler, { passive: true });

        // Capturing scroll delegation on el to observe .think-content without leaking listeners
        state.thinkScrollHandler = (e) => {
            const target = e.target;
            if (target && target.classList && target.classList.contains('think-content')) {
                const distanceFromBottom = target.scrollHeight - target.scrollTop - target.clientHeight;
                target._userScrolledUp = (distanceFromBottom > thinkThreshold);
            }
        };
        el.addEventListener('scroll', state.thinkScrollHandler, { capture: true, passive: true });

        // When user toggles a <details> (reasoning trace or tool trace), keep scroll position pinned if at bottom
        state.toggleHandler = () => {
            if (!state.userScrolledUp) {
                scheduleRafUpdate();
            } else {
                updateScrollBtn();
            }
        };
        el.addEventListener('toggle', state.toggleHandler, true);

        if (scrollBtn) {
            state.btnHandler = (e) => {
                e.preventDefault();
                window.NightCodeForge.scrollToBottom(elementId, true, true);
            };
            scrollBtn.addEventListener('click', state.btnHandler);
        }

        // MutationObserver watches for DOM updates (tokens streaming in, new messages added)
        state.observer = new MutationObserver((mutations) => {
            for (let i = 0; i < mutations.length; i++) {
                let node = mutations[i].target;
                if (node) {
                    if (node.nodeType === 3) { // Node.TEXT_NODE
                        node = node.parentElement;
                    }
                    if (node && node.closest) {
                        const thinkEl = node.closest('.think-content');
                        if (thinkEl) {
                            state.mutatedThinkEls.add(thinkEl);
                        }
                    }
                }
            }

            // Also check for active live streaming thinking containers
            const activeLiveThinks = el.querySelectorAll('.turn-live-thinking .think-content, .chat-bubble.streaming .think-content, .streaming-message .think-content');
            for (let j = 0; j < activeLiveThinks.length; j++) {
                state.mutatedThinkEls.add(activeLiveThinks[j]);
            }

            scheduleRafUpdate();
        });

        state.observer.observe(el, {
            childList: true,
            subtree: true,
            characterData: true,
            attributes: true,
            attributeFilter: ['open', 'style', 'class']
        });

        // ResizeObserver catches container/window resizing or dynamic reflows
        if (window.ResizeObserver) {
            state.resizeObserver = new ResizeObserver(() => {
                if (!state.userScrolledUp) {
                    scheduleRafUpdate();
                } else {
                    updateScrollBtn();
                }
            });
            state.resizeObserver.observe(el);
            if (state.scrollContainer !== el) {
                state.resizeObserver.observe(state.scrollContainer);
            }
        }

        activeScrollStates.set(elementId, state);

        // Initial scroll to bottom only if autoScrollOnByDefault is true
        requestAnimationFrame(() => {
            if (autoScrollOnByDefault && state.scrollContainer) {
                state.scrollContainer.scrollTop = state.scrollContainer.scrollHeight;
            }
            updateScrollBtn();
        });
    };

    /**
     * Resets or pauses auto-scrolling on a container without forcing a jump unless requested.
     * @param {string} elementId - The container element ID.
     * @param {boolean} [enabled=false] - Whether auto-scrolling is enabled.
     */
    window.NightCodeForge.resetAutoScroll = function (elementId, enabled = false) {
        const state = activeScrollStates.get(elementId);
        if (!state) return;

        state.userScrolledUp = !enabled;
        requestAnimationFrame(() => {
            if (enabled && state.scrollContainer) {
                state.scrollContainer.scrollTop = state.scrollContainer.scrollHeight;
            }
            if (state.updateScrollBtn) {
                state.updateScrollBtn();
            }
        });
    };

    /**
     * Disposes and unbinds all observers and event listeners for a container.
     * @param {string} elementId
     */
    window.NightCodeForge.disposeAutoScroll = function (elementId) {
        if (!activeScrollStates.has(elementId)) return;
        const state = activeScrollStates.get(elementId);
        const el = document.getElementById(elementId) || state.el;

        state.rafPending = false;
        state.mutatedThinkEls.clear();

        if (state.observer) {
            state.observer.disconnect();
            state.observer = null;
        }
        if (state.resizeObserver) {
            state.resizeObserver.disconnect();
            state.resizeObserver = null;
        }
        if (state.scrollContainer && state.scrollHandler) {
            state.scrollContainer.removeEventListener('scroll', state.scrollHandler);
        }
        if (el && state.thinkScrollHandler) {
            el.removeEventListener('scroll', state.thinkScrollHandler, { capture: true });
        }
        if (el && state.toggleHandler) {
            el.removeEventListener('toggle', state.toggleHandler, true);
        }
        if (state.scrollBtn && state.btnHandler) {
            state.scrollBtn.removeEventListener('click', state.btnHandler);
        }

        activeScrollStates.delete(elementId);
    };

    /**
     * Downloads plain text content as a file in the browser.
     * @param {string} filename
     * @param {string} content
     */
    window.NightCodeForge.downloadTextFile = function (filename, content) {
        const blob = new Blob([content], { type: 'text/plain;charset=utf-8;' });
        const url = URL.createObjectURL(blob);
        const link = document.createElement('a');
        link.href = url;
        link.download = filename;
        document.body.appendChild(link);
        link.click();
        document.body.removeChild(link);
        URL.revokeObjectURL(url);
    };

    /**
     * Scrolls an element into view and keeps it highlighted (deep-link support).
     * @param {string} elementId
     */
    window.NightCodeForge.scrollToElement = function (elementId) {
        const el = document.getElementById(elementId);
        if (!el) return;
        el.scrollIntoView({ behavior: 'smooth', block: 'center' });
    };

    /**
     * Scrolls a target code line element into view while respecting prefers-reduced-motion.
     * @param {string} elementId
     */
    window.NightCodeForge.scrollCodeLineIntoView = function (elementId) {
        const el = document.getElementById(elementId);
        if (!el || typeof el.scrollIntoView !== 'function') return;

        const prefersReducedMotion = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
        el.scrollIntoView({
            behavior: prefersReducedMotion ? 'auto' : 'smooth',
            block: 'center',
            inline: 'nearest'
        });
    };

    /**
     * Copies text to the browser clipboard using the Clipboard API.
     * @param {string} text
     * @returns {Promise<boolean>}
     */
    window.NightCodeForge.copyTextToClipboard = async function (text) {
        if (!text || !navigator.clipboard || typeof navigator.clipboard.writeText !== 'function') {
            return false;
        }
        try {
            await navigator.clipboard.writeText(text);
            return true;
        } catch (err) {
            console.warn('NightCodeForge: clipboard copy failed', err);
            return false;
        }
    };

    const STICKY_TOP_OFFSET_PX = 16;
    const VIEWPORT_BOTTOM_VISIBILITY_MARGIN_PX = 120;

    /**
     * Resets any internal vertical scroll on the target element and scrolls its top edge into view
     * within .forge-main-content only when its top edge is scrolled above the viewport or sits below the visible fold.
     * @param {string} elementId
     */
    window.NightCodeForge.scrollElementTopIntoViewIfNeeded = function (elementId) {
        const el = document.getElementById(elementId);
        if (!el) return;

        const prefersReducedMotion = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
        const behavior = prefersReducedMotion ? 'auto' : 'smooth';

        if (el.scrollTop > 0) {
            el.scrollTop = 0;
        }

        const elRect = el.getBoundingClientRect();

        const scrollContainer = typeof el.closest === 'function' ? el.closest('.forge-main-content') : null;
        if (!scrollContainer) {
            const effectiveWindowMargin = Math.min(VIEWPORT_BOTTOM_VISIBILITY_MARGIN_PX, window.innerHeight / 2);
            if ((elRect.top < 0 || elRect.top > window.innerHeight - effectiveWindowMargin) &&
                typeof el.scrollIntoView === 'function') {
                el.scrollIntoView({ behavior: behavior, block: 'start' });
            }
            return;
        }

        const containerRect = scrollContainer.getBoundingClientRect();
        if (containerRect.height <= 0) {
            return;
        }

        const effectiveMargin = Math.min(VIEWPORT_BOTTOM_VISIBILITY_MARGIN_PX, containerRect.height / 2);
        const isTopAboveViewport = elRect.top < containerRect.top;
        const isTopBelowFold = elRect.top > containerRect.bottom - effectiveMargin;

        if (!isTopAboveViewport && !isTopBelowFold) {
            return;
        }

        const computedScrollMarginTop = window.getComputedStyle
            ? parseFloat(window.getComputedStyle(el).scrollMarginTop)
            : NaN;
        const topOffsetPx = Number.isFinite(computedScrollMarginTop) && computedScrollMarginTop > 0
            ? computedScrollMarginTop
            : STICKY_TOP_OFFSET_PX;

        const targetTop = scrollContainer.scrollTop + (elRect.top - containerRect.top) - topOffsetPx;
        const clampedTop = Math.max(0, targetTop);
        if (typeof scrollContainer.scrollTo === 'function') {
            scrollContainer.scrollTo({ top: clampedTop, behavior: behavior });
        } else {
            scrollContainer.scrollTop = clampedTop;
        }
    };

    let foregroundHandler = null;

    /**
     * Notifies the Blazor circuit whenever a (mobile) browser tab returns to the foreground
     * so that active task status can be refreshed.
     * @param {object} dotNetRef - DotNetObjectReference exposing OnTabForegrounded().
     */
    window.NightCodeForge.registerForegroundSync = function (dotNetRef) {
        window.NightCodeForge.unregisterForegroundSync();
        foregroundHandler = function () {
            if (document.visibilityState === 'visible') {
                dotNetRef.invokeMethodAsync('OnTabForegrounded').catch(function (err) {
                    console.warn('NightCodeForge: foreground sync failed', err);
                });
            }
        };
        document.addEventListener('visibilitychange', foregroundHandler);
    };

    window.NightCodeForge.unregisterForegroundSync = function () {
        if (foregroundHandler) {
            document.removeEventListener('visibilitychange', foregroundHandler);
            foregroundHandler = null;
        }
    };

    const activePasteHandlers = new Map();

    /**
     * Attaches a clipboard paste listener on a composer textarea that intercepts image items
     * and dispatches decoded base64 bytes to Blazor without interfering with normal text paste.
     * @param {string} elementId - The DOM id of the composer textarea.
     * @param {object} dotNetRef - DotNetObjectReference exposing OnClipboardImagePastedAsync(fileName, contentType, base64) and OnClipboardImageRejectedAsync(fileName, errorMessage).
     * @param {number} [maxBytes] - Maximum allowed clipboard image byte length before reading into base64.
     */
    window.NightCodeForge.initClipboardImagePaste = function (elementId, dotNetRef, maxBytes) {
        window.NightCodeForge.disposeClipboardImagePaste(elementId);
        const el = document.getElementById(elementId);
        if (!el || !dotNetRef) return;

        const effectiveMaxBytes = (typeof maxBytes === 'number' && maxBytes > 0) ? maxBytes : (10 * 1024 * 1024);

        const handler = function (e) {
            const items = e.clipboardData && e.clipboardData.items;
            if (!items || items.length === 0) return;

            for (let i = 0; i < items.length; i++) {
                const item = items[i];
                if (item && item.kind === 'file' && item.type && item.type.startsWith('image/')) {
                    const blob = item.getAsFile();
                    if (!blob) continue;

                    e.preventDefault();
                    const mimeType = blob.type || item.type || 'image/png';
                    const fileName = blob.name || ('clipboard-' + Date.now() + '.' + (mimeType.split('/')[1] || 'png'));

                    if (typeof blob.size === 'number' && blob.size > effectiveMaxBytes) {
                        const mbSize = (blob.size / (1024 * 1024)).toFixed(1);
                        const mbMax = Math.round(effectiveMaxBytes / (1024 * 1024));
                        const errorMsg = 'Pasted image (' + mbSize + ' MB) exceeds maximum allowed vision image size of ' + mbMax + ' MB.';
                        dotNetRef.invokeMethodAsync('OnClipboardImageRejectedAsync', fileName, errorMsg).catch(function (err) {
                            console.warn('NightCodeForge: clipboard image rejection callback failed', err);
                        });
                        return;
                    }

                    const reader = new FileReader();
                    reader.onload = function () {
                        const result = typeof reader.result === 'string' ? reader.result : '';
                        const commaIdx = result.indexOf(',');
                        const base64 = commaIdx >= 0 ? result.substring(commaIdx + 1) : result;
                        if (base64) {
                            dotNetRef.invokeMethodAsync('OnClipboardImagePastedAsync', fileName, mimeType, base64).catch(function (err) {
                                console.warn('NightCodeForge: clipboard image upload failed', err);
                            });
                        }
                    };
                    reader.readAsDataURL(blob);
                    return;
                }
            }
        };

        el.addEventListener('paste', handler);
        activePasteHandlers.set(elementId, { el: el, handler: handler });
    };

    window.NightCodeForge.disposeClipboardImagePaste = function (elementId) {
        const entry = activePasteHandlers.get(elementId);
        if (entry && entry.el && entry.handler) {
            entry.el.removeEventListener('paste', entry.handler);
        }
        activePasteHandlers.delete(elementId);
    };

    /**
     * Registers the PWA service worker when supported and running in a secure context or loopback.
     */
    window.NightCodeForge.registerServiceWorker = function () {
        if ('serviceWorker' in navigator && (window.isSecureContext || location.hostname === 'localhost' || location.hostname === '127.0.0.1' || location.hostname === '[::1]')) {
            navigator.serviceWorker.register('/service-worker.js').catch(function (err) {
                console.warn('Service worker registration failed:', err);
            });
        }
    };

    window.NightCodeForge.registerServiceWorker();
})();

