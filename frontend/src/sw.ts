/// <reference lib="webworker" />
import { precacheAndRoute } from 'workbox-precaching';
import { clientsClaim } from 'workbox-core';

declare const self: ServiceWorkerGlobalScope & {
  __WB_MANIFEST: (string | { url: string; revision: string | null })[];
};

// Precache the app shell (injected by vite-plugin-pwa at build time).
precacheAndRoute(self.__WB_MANIFEST);

// registerType: 'autoUpdate' — take over as soon as a new SW is available.
self.skipWaiting();
clientsClaim();

// Notification payload contract sent by the server push sender.
type PushPayload = {
  title?: string;
  body?: string;
  url?: string;
  tag?: string;
  icon?: string;
};

self.addEventListener('push', (event) => {
  let payload: PushPayload = {};
  if (event.data) {
    try {
      payload = event.data.json() as PushPayload;
    } catch {
      payload = { body: event.data.text() };
    }
  }

  const title = payload.title ?? 'Aspire PR Focus';
  const options: NotificationOptions = {
    body: payload.body ?? '',
    icon: payload.icon ?? '/pwa-192x192.png',
    badge: '/pwa-192x192.png',
    tag: payload.tag,
    data: { url: payload.url ?? '/' },
  };

  event.waitUntil(self.registration.showNotification(title, options));
});

self.addEventListener('notificationclick', (event) => {
  event.notification.close();

  const data = event.notification.data as { url?: string } | undefined;
  const targetUrl = new URL(data?.url ?? '/', self.registration.scope).href;

  event.waitUntil(
    (async () => {
      const clientList = await self.clients.matchAll({
        type: 'window',
        includeUncontrolled: true,
      });

      // Prefer focusing an already-open tab and routing it to the PR (hash navigation).
      for (const client of clientList) {
        if ('focus' in client) {
          await client.focus();
          if ('navigate' in client) {
            try {
              await client.navigate(targetUrl);
            } catch {
              // Ignore navigation failures (e.g. unsupported); the tab is at least focused.
            }
          }
          return;
        }
      }

      await self.clients.openWindow(targetUrl);
    })(),
  );
});

// Browsers can rotate a push subscription; re-subscribe and re-register it server-side.
self.addEventListener('pushsubscriptionchange', (event) => {
  event.waitUntil(
    (async () => {
      try {
        const keyResponse = await fetch('/api/notifications/vapid-public-key');
        if (!keyResponse.ok) {
          return;
        }

        const { publicKey } = (await keyResponse.json()) as { publicKey: string };
        const subscription = await self.registration.pushManager.subscribe({
          userVisibleOnly: true,
          applicationServerKey: urlBase64ToUint8Array(publicKey),
        });

        await fetch('/api/notifications/subscribe', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(subscription),
        });
      } catch {
        // Best-effort: the settings UI re-syncs the subscription on next open.
      }
    })(),
  );
});

function urlBase64ToUint8Array(base64String: string): Uint8Array<ArrayBuffer> {
  const padding = '='.repeat((4 - (base64String.length % 4)) % 4);
  const base64 = (base64String + padding).replace(/-/g, '+').replace(/_/g, '/');
  const rawData = atob(base64);
  const buffer = new ArrayBuffer(rawData.length);
  const outputArray = new Uint8Array(buffer);
  for (let i = 0; i < rawData.length; i += 1) {
    outputArray[i] = rawData.charCodeAt(i);
  }
  return outputArray;
}
