// NightCodeForge Web Notifications Helper (Step 12.8C)
(() => {
    'use strict';
    window.NightCodeForge = window.NightCodeForge || {};

    const PREFS_STORAGE_KEY = 'ncf_notification_preferences_v1';
    const DEDUP_WINDOW_MS = 2000;
    const DEFAULT_PREFERENCES = {
        masterEnabled: true,
        notifyOnQuestionInbox: true,
        notifyOnReviewRoom: true,
        notifyOnTaskMerged: true
    };

    function isSupported() {
        if (typeof window === 'undefined') return false;
        const hasNotification = 'Notification' in window;
        const hasSW = 'serviceWorker' in navigator && typeof window.ServiceWorkerRegistration !== 'undefined' && 'showNotification' in window.ServiceWorkerRegistration.prototype;
        return hasNotification || hasSW;
    }

    function getPermissionState() {
        if (!isSupported()) {
            return 'unsupported';
        }
        if ('Notification' in window) {
            return Notification.permission; // 'granted', 'denied', or 'default'
        }
        return 'default';
    }

    async function requestPermission() {
        if (!isSupported()) {
            return 'unsupported';
        }
        if ('Notification' in window && typeof Notification.requestPermission === 'function') {
            try {
                return await Notification.requestPermission();
            } catch (err) {
                console.warn('NightCodeForge: Notification.requestPermission error', err);
                return Notification.permission;
            }
        }
        return 'unsupported';
    }

    function isDocumentVisible() {
        return typeof document !== 'undefined' && document.visibilityState === 'visible';
    }

    function getPreferences() {
        try {
            const raw = localStorage.getItem(PREFS_STORAGE_KEY);
            if (!raw) {
                return Object.assign({}, DEFAULT_PREFERENCES);
            }
            const parsed = JSON.parse(raw);
            return {
                masterEnabled: parsed.masterEnabled ?? parsed.MasterEnabled ?? true,
                notifyOnQuestionInbox: parsed.notifyOnQuestionInbox ?? parsed.NotifyOnQuestionInbox ?? true,
                notifyOnReviewRoom: parsed.notifyOnReviewRoom ?? parsed.NotifyOnReviewRoom ?? true,
                notifyOnTaskMerged: parsed.notifyOnTaskMerged ?? parsed.NotifyOnTaskMerged ?? true
            };
        } catch (err) {
            console.warn('NightCodeForge: getPreferences failed', err);
            return Object.assign({}, DEFAULT_PREFERENCES);
        }
    }

    function savePreferences(prefs) {
        try {
            if (!prefs) return false;
            const normalized = {
                masterEnabled: prefs.masterEnabled ?? prefs.MasterEnabled ?? true,
                notifyOnQuestionInbox: prefs.notifyOnQuestionInbox ?? prefs.NotifyOnQuestionInbox ?? true,
                notifyOnReviewRoom: prefs.notifyOnReviewRoom ?? prefs.NotifyOnReviewRoom ?? true,
                notifyOnTaskMerged: prefs.notifyOnTaskMerged ?? prefs.NotifyOnTaskMerged ?? true
            };
            localStorage.setItem(PREFS_STORAGE_KEY, JSON.stringify(normalized));
            return true;
        } catch (err) {
            console.warn('NightCodeForge: savePreferences failed', err);
            return false;
        }
    }

    async function showNotification(title, options) {
        if (getPermissionState() !== 'granted') {
            return false;
        }

        // Multi-tab deduplication guard (2-second window across open tabs)
        const dedupTag = (options && options.tag) || (title + ':' + (options && options.body ? options.body : ''));
        const dedupKey = 'ncf_dedup_' + dedupTag;
        const now = Date.now();
        try {
            const last = localStorage.getItem(dedupKey);
            if (last) {
                const diff = now - parseInt(last, 10);
                if (diff >= 0 && diff < DEDUP_WINDOW_MS) {
                    return false;
                }
            }
            localStorage.setItem(dedupKey, now.toString());
        } catch (err) {
            console.warn('NightCodeForge: localStorage deduplication check failed', err);
        }

        const opts = Object.assign({
            icon: '/icon-192.png',
            badge: '/icon-192.png'
        }, options || {});

        // Prefer ServiceWorkerRegistration.showNotification() for mobile and PWA compliance
        if ('serviceWorker' in navigator) {
            try {
                const reg = await navigator.serviceWorker.ready;
                if (reg && typeof reg.showNotification === 'function') {
                    await reg.showNotification(title, opts);
                    return true;
                }
            } catch (swErr) {
                console.warn('NightCodeForge: SW showNotification failed, attempting desktop fallback', swErr);
            }
        }

        // Desktop window context fallback
        if ('Notification' in window) {
            try {
                const notif = new Notification(title, opts);
                if (opts.data && opts.data.url) {
                    notif.onclick = function () {
                        window.focus();
                        window.location.href = opts.data.url;
                        notif.close();
                    };
                }
                return true;
            } catch (desktopErr) {
                console.warn('NightCodeForge: Desktop Notification constructor failed', desktopErr);
            }
        }

        return false;
    }

    window.NightCodeForge.notifications = {
        isSupported,
        getPermissionState,
        requestPermission,
        isDocumentVisible,
        getPreferences,
        savePreferences,
        showNotification
    };
})();
