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
     */
    window.NightCodeForge.scrollToBottom = function (elementId, force = true) {
        const el = document.getElementById(elementId);
        if (!el) return;

        const state = activeScrollStates.get(elementId);
        if (state && force) {
            state.userScrolledUp = false;
            if (state.scrollBtn) {
                state.scrollBtn.style.display = 'none';
            }
        }

        requestAnimationFrame(() => {
            el.scrollTop = el.scrollHeight;
        });
    };

    /**
     * Initializes smart auto-scrolling on a chat transcript container.
     * - Keeps the container pinned to bottom when new streaming chunks, thinking traces, or messages arrive.
     * - Pauses auto-scroll if the user scrolls up to read history.
     * - Displays an optional floating 'scroll to bottom' button when user is scrolled up.
     * - Resumes auto-scrolling when user returns to bottom or clicks the button.
     *
     * @param {string} elementId - The ID of the container (e.g. 'chatMessagesContainer')
     * @param {string} [scrollBtnId] - Optional ID of floating scroll-to-bottom button
     */
    window.NightCodeForge.initAutoScroll = function (elementId, scrollBtnId) {
        const el = document.getElementById(elementId);
        if (!el) return;

        // Clean up any previous state attached to this element
        window.NightCodeForge.disposeAutoScroll(elementId);

        const scrollBtn = scrollBtnId ? document.getElementById(scrollBtnId) : null;
        const state = {
            userScrolledUp: false,
            scrollBtn: scrollBtn,
            observer: null,
            resizeObserver: null,
            scrollHandler: null,
            btnHandler: null,
            toggleHandler: null
        };

        const threshold = 100; // px threshold from bottom to consider "at bottom"

        const updateScrollBtn = () => {
            if (!scrollBtn) return;
            const isScrollable = el.scrollHeight > el.clientHeight + threshold;
            if (state.userScrolledUp && isScrollable) {
                scrollBtn.style.display = 'flex';
            } else {
                scrollBtn.style.display = 'none';
            }
        };

        state.scrollHandler = () => {
            const distanceFromBottom = el.scrollHeight - el.scrollTop - el.clientHeight;
            if (distanceFromBottom > threshold) {
                state.userScrolledUp = true;
            } else {
                state.userScrolledUp = false;
            }
            updateScrollBtn();
        };

        el.addEventListener('scroll', state.scrollHandler, { passive: true });

        // When user toggles a <details> (reasoning trace or tool trace), keep scroll position pinned if at bottom
        state.toggleHandler = () => {
            if (!state.userScrolledUp) {
                requestAnimationFrame(() => {
                    el.scrollTop = el.scrollHeight;
                });
            }
            updateScrollBtn();
        };
        el.addEventListener('toggle', state.toggleHandler, true);

        if (scrollBtn) {
            state.btnHandler = (e) => {
                e.preventDefault();
                state.userScrolledUp = false;
                updateScrollBtn();
                el.scrollTo({ top: el.scrollHeight, behavior: 'smooth' });
            };
            scrollBtn.addEventListener('click', state.btnHandler);
        }

        // MutationObserver watches for DOM updates (tokens streaming in, new messages added)
        state.observer = new MutationObserver(() => {
            if (!state.userScrolledUp) {
                el.scrollTop = el.scrollHeight;
            }
            updateScrollBtn();
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
                    el.scrollTop = el.scrollHeight;
                }
                updateScrollBtn();
            });
            state.resizeObserver.observe(el);
        }

        activeScrollStates.set(elementId, state);

        // Immediate scroll to bottom on initialization
        requestAnimationFrame(() => {
            el.scrollTop = el.scrollHeight;
            updateScrollBtn();
        });
    };

    /**
     * Disposes and unbinds all observers and event listeners for a container.
     * @param {string} elementId
     */
    window.NightCodeForge.disposeAutoScroll = function (elementId) {
        if (!activeScrollStates.has(elementId)) return;
        const state = activeScrollStates.get(elementId);
        const el = document.getElementById(elementId);

        if (state.observer) {
            state.observer.disconnect();
            state.observer = null;
        }
        if (state.resizeObserver) {
            state.resizeObserver.disconnect();
            state.resizeObserver = null;
        }
        if (el && state.scrollHandler) {
            el.removeEventListener('scroll', state.scrollHandler);
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
})();

