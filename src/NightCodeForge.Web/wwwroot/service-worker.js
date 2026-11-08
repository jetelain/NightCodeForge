// NightCodeForge PWA & Notification Service Worker
const CACHE_NAME = 'ncf-cache-v1';
const PRECACHE_URLS = [
    '/',
    '/css/fonts.css',
    '/css/theme.css',
    '/css/app.css',
    '/favicon.svg',
    '/favicon.png',
    '/icon-192.png',
    '/icon-512.png',
    '/manifest.webmanifest',
    '/js/app.js',
    '/js/notifications.js'
];

self.addEventListener('install', (event) => {
    event.waitUntil(
        caches.open(CACHE_NAME).then((cache) => cache.addAll(PRECACHE_URLS))
    );
    self.skipWaiting();
});

self.addEventListener('activate', (event) => {
    event.waitUntil(
        caches.keys().then((keys) =>
            Promise.all(
                keys.filter((k) => k !== CACHE_NAME).map((k) => caches.delete(k))
            )
        )
    );
    self.clients.claim();
});

// Network-first with cache fallback for GET requests; bypass Blazor Server, SignalR, and Hangfire
self.addEventListener('fetch', (event) => {
    if (event.request.method !== 'GET') {
        return;
    }
    const url = new URL(event.request.url);
    if (url.origin !== self.location.origin ||
        url.pathname.startsWith('/_blazor') ||
        url.pathname.startsWith('/hubs/') ||
        url.pathname.startsWith('/hangfire')) {
        return;
    }
    event.respondWith(
        fetch(event.request).catch(async () => {
            const cached = await caches.match(event.request);
            if (cached) {
                return cached;
            }
            // For navigational requests to sub-routes while offline, return the cached app shell (/)
            if (event.request.mode === 'navigate') {
                const shell = await caches.match('/');
                if (shell) {
                    return shell;
                }
            }
            return Response.error();
        })
    );
});

