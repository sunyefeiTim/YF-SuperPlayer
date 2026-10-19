self.importScripts('./service-worker-assets.js');
const cacheName = 'yf-super-player-' + self.assetsManifest.version;
const offlineAssets = self.assetsManifest.assets
  .filter(asset => !asset.url.endsWith('.pdb'))
  .map(asset => new Request(asset.url, { integrity: asset.hash, cache: 'no-cache' }));
self.addEventListener('install', event => event.waitUntil(caches.open(cacheName).then(cache => cache.addAll(offlineAssets))));
self.addEventListener('activate', event => event.waitUntil(caches.keys().then(keys => Promise.all(keys.filter(key => key !== cacheName).map(key => caches.delete(key))))));
self.addEventListener('fetch', event => event.respondWith(caches.match(event.request).then(cached => cached || fetch(event.request))));
