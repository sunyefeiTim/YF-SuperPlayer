window.yfPlayer = (() => {
    let dotnet;
    let audio;
    const objectUrls = new Set();
    const trackUrls = new Map();
    const databaseName = "yf-super-player";
    const trackStoreName = "tracks";
    const playbackMemoryKey = "yf-player-playback-memory";
    let activeTrackId = "";
    let lastPersistedSecond = -1;

    function savePlaybackMemory(force = false) {
        if (!audio || !activeTrackId) return;
        const second = Math.max(0, Math.floor(Number.isFinite(audio.currentTime) ? audio.currentTime : 0));
        if (!force && second === lastPersistedSecond) return;
        lastPersistedSecond = second;
        localStorage.setItem(playbackMemoryKey, JSON.stringify({
            trackId: activeTrackId,
            currentSeconds: Number.isFinite(audio.currentTime) ? audio.currentTime : 0,
            durationSeconds: Number.isFinite(audio.duration) ? audio.duration : 0
        }));
    }

    function openDatabase() {
        return new Promise((resolve, reject) => {
            const request = indexedDB.open(databaseName, 1);
            request.onupgradeneeded = () => {
                const db = request.result;
                if (!db.objectStoreNames.contains(trackStoreName))
                    db.createObjectStore(trackStoreName, { keyPath: "id" });
            };
            request.onsuccess = () => resolve(request.result);
            request.onerror = () => reject(request.error);
        });
    }

    async function withStore(mode, action) {
        const db = await openDatabase();
        try {
            return await new Promise((resolve, reject) => {
                const transaction = db.transaction(trackStoreName, mode);
                const store = transaction.objectStore(trackStoreName);
                let result;
                try { result = action(store); } catch (error) { reject(error); return; }
                transaction.oncomplete = () => resolve(result);
                transaction.onerror = () => reject(transaction.error);
                transaction.onabort = () => reject(transaction.error);
            });
        } finally {
            db.close();
        }
    }

    function createTrackUrl(track) {
        const oldUrl = trackUrls.get(track.id);
        if (oldUrl) return oldUrl;
        const format = String(track.fileType || "").toLowerCase();
        const expectedType = format === "flac" ? "audio/flac"
            : format === "mp3" ? "audio/mpeg" : track.blob.type;
        const playableBlob = expectedType && track.blob.type !== expectedType
            ? new Blob([track.blob], { type: expectedType }) : track.blob;
        const url = URL.createObjectURL(playableBlob);
        trackUrls.set(track.id, url);
        objectUrls.add(url);
        return url;
    }

    function toClientTrack(track) {
        return {
            id: track.id,
            title: track.title,
            url: createTrackUrl(track),
            fileType: track.fileType
        };
    }

    function notify() {
        if (!dotnet || !audio) return;
        savePlaybackMemory(audio.paused);
        dotnet.invokeMethodAsync("OnPlaybackState",
            Number.isFinite(audio.currentTime) ? audio.currentTime : 0,
            Number.isFinite(audio.duration) ? audio.duration : 0,
            audio.paused).catch(() => {});
    }

    return {
        initialize(reference) {
            dotnet = reference;
            audio = document.getElementById("yf-audio");
            ["timeupdate", "durationchange", "play", "pause", "loadedmetadata", "seeked"].forEach(name =>
                audio.addEventListener(name, notify));
            audio.addEventListener("ended", () => dotnet?.invokeMethodAsync("OnTrackEnded").catch(() => {}));
            if ("mediaSession" in navigator) {
                navigator.mediaSession.setActionHandler("play", () => audio.play());
                navigator.mediaSession.setActionHandler("pause", () => audio.pause());
                navigator.mediaSession.setActionHandler("seekto", details => {
                    if (typeof details.seekTime === "number") audio.currentTime = details.seekTime;
                });
                navigator.mediaSession.setActionHandler("previoustrack", () => dotnet?.invokeMethodAsync("OnMediaPrevious"));
                navigator.mediaSession.setActionHandler("nexttrack", () => dotnet?.invokeMethodAsync("OnMediaNext"));
            }
        },
        async importFiles(input) {
            const imported = [];
            for (const file of Array.from(input.files || [])) {
                const title = file.name.replace(/\.[^.]+$/, "");
                const track = {
                    id: `${file.name}|${file.size}|${file.lastModified}`,
                    title,
                    fileName: file.name,
                    fileType: (file.name.split(".").pop() || "audio").toUpperCase(),
                    blob: file,
                    savedAt: Date.now()
                };
                await withStore("readwrite", store => store.put(track));
                imported.push(toClientTrack(track));
            }
            input.value = "";
            navigator.storage?.persist?.().catch(() => {});
            return imported;
        },
        async getSavedTracks() {
            const records = await new Promise(async (resolve, reject) => {
                const db = await openDatabase();
                const transaction = db.transaction(trackStoreName, "readonly");
                const request = transaction.objectStore(trackStoreName).getAll();
                request.onsuccess = () => resolve(request.result || []);
                request.onerror = () => reject(request.error);
                transaction.oncomplete = () => db.close();
            });
            return records.sort((a, b) => a.savedAt - b.savedAt).map(toClientTrack);
        },
        async getStorageInfo() {
            const records = await new Promise(async (resolve, reject) => {
                const db = await openDatabase();
                const transaction = db.transaction(trackStoreName, "readonly");
                const request = transaction.objectStore(trackStoreName).getAll();
                request.onsuccess = () => resolve(request.result || []);
                request.onerror = () => reject(request.error);
                transaction.oncomplete = () => db.close();
            });
            const estimate = await navigator.storage?.estimate?.().catch(() => null);
            return {
                trackBytes: records.reduce((sum, track) => sum + Number(track.blob?.size || 0), 0),
                quotaBytes: Number(estimate?.quota || 0)
            };
        },
        async deleteTrack(id) {
            if (activeTrackId === id) {
                audio.pause();
                audio.removeAttribute("src");
                audio.load();
                activeTrackId = "";
                localStorage.removeItem(playbackMemoryKey);
            }
            await withStore("readwrite", store => store.delete(id));
            const url = trackUrls.get(id);
            if (url) {
                URL.revokeObjectURL(url);
                objectUrls.delete(url);
                trackUrls.delete(id);
            }
        },
        getPlaybackMemory() {
            try {
                const value = JSON.parse(localStorage.getItem(playbackMemoryKey) || "{}");
                return {
                    trackId: typeof value.trackId === "string" ? value.trackId : "",
                    currentSeconds: Number.isFinite(value.currentSeconds) ? value.currentSeconds : 0,
                    durationSeconds: Number.isFinite(value.durationSeconds) ? value.durationSeconds : 0
                };
            } catch {
                return { trackId: "", currentSeconds: 0, durationSeconds: 0 };
            }
        },
        async downloadTrack(id) {
            const track = await new Promise(async (resolve, reject) => {
                const db = await openDatabase();
                const transaction = db.transaction(trackStoreName, "readonly");
                const request = transaction.objectStore(trackStoreName).get(id);
                request.onsuccess = () => resolve(request.result);
                request.onerror = () => reject(request.error);
                transaction.oncomplete = () => db.close();
            });
            if (!track?.blob) return;
            const url = createTrackUrl(track);
            const link = document.createElement("a");
            link.href = url;
            link.download = track.fileName || `${track.title}.${String(track.fileType || "audio").toLowerCase()}`;
            document.body.appendChild(link);
            link.click();
            link.remove();
        },
        async downloadOnlineTrack(endpoint, request) {
            const response = await fetch(endpoint, {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify(request)
            });
            if (!response.ok) {
                let message = "下载失败";
                try { message = (await response.json()).message || message; } catch { }
                throw new Error(message);
            }
            const receivedBlob = await response.blob();
            const expectedType = String(request.format).toLowerCase() === "flac" ? "audio/flac" : "audio/mpeg";
            const blob = receivedBlob.type === expectedType
                ? receivedBlob : new Blob([receivedBlob], { type: expectedType });
            const disposition = response.headers.get("Content-Disposition") || "";
            const encoded = disposition.match(/filename\*=UTF-8''([^;]+)/i)?.[1];
            const quoted = disposition.match(/filename="?([^";]+)"?/i)?.[1];
            const fileName = encoded ? decodeURIComponent(encoded) : (quoted || `song-${Date.now()}.mp3`);
            const title = fileName.replace(/\.[^.]+$/, "").replace(/-/g, " - ");
            const extension = (fileName.split(".").pop() || "audio").toUpperCase();
            const track = {
                id: `online|${request.platform}|${request.id}|${request.format}|${request.bitrate || ""}`,
                title,
                fileName,
                fileType: extension,
                blob,
                savedAt: Date.now()
            };
            await withStore("readwrite", store => store.put(track));
            navigator.storage?.persist?.().catch(() => {});
            return toClientTrack(track);
        },
        openPicker(input) { input?.click(); },
        async playTrack(url, title, artist, artwork, trackId, startSeconds) {
            activeTrackId = trackId || "";
            lastPersistedSecond = -1;
            audio.src = url;
            audio.load();
            if (Number.isFinite(startSeconds) && startSeconds > 0) {
                audio.addEventListener("loadedmetadata", () => {
                    audio.currentTime = Math.min(startSeconds, Math.max(0, audio.duration - 0.1));
                }, { once: true });
            }
            if ("mediaSession" in navigator) {
                navigator.mediaSession.metadata = new MediaMetadata({
                    title,
                    artist,
                    album: "YF-Super Player",
                    artwork: [{ src: artwork, sizes: "1254x1254", type: "image/png" }]
                });
            }
            await audio.play();
        },
        async toggle() { if (audio.paused) await audio.play(); else audio.pause(); },
        seek(seconds) { if (Number.isFinite(seconds)) audio.currentTime = Math.max(0, seconds); },
        dispose() {
            savePlaybackMemory(true);
            audio?.pause();
            objectUrls.forEach(url => URL.revokeObjectURL(url));
            objectUrls.clear();
            trackUrls.clear();
            dotnet = null;
        }
    };
})();
