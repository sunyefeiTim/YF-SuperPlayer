self.addEventListener('install', event => event.waitUntil(self.skipWaiting()));
self.addEventListener('activate', event => event.waitUntil((async () => {
  await Promise.all((await caches.keys()).map(key => caches.delete(key)));
  await self.registration.unregister();
  for (const client of await self.clients.matchAll({ type: 'window', includeUncontrolled: true }))
    client.navigate('https://yf-superplayer-api-sunyefeitim.onrender.com/');
})()));
