// Persistent storage for the browser build: mirrors the game's AppData folder (saves, options)
// into IndexedDB, because .NET's in-browser file system lives only in memory.
// Files are stored as base64 strings keyed by their path relative to the AppData folder.

const DB_NAME = 'stardew-web';
const STORE = 'files';
let db = null;
let queue = Promise.resolve();   // writes are applied in order

function request(r) {
    return new Promise((resolve, reject) => {
        r.onsuccess = () => resolve(r.result);
        r.onerror = () => reject(r.error);
    });
}

export async function open() {
    db = await new Promise((resolve, reject) => {
        const r = indexedDB.open(DB_NAME, 1);
        r.onupgradeneeded = () => r.result.createObjectStore(STORE);
        r.onsuccess = () => resolve(r.result);
        r.onerror = () => reject(r.error);
    });
    // Ask the browser not to evict our data under storage pressure (granted silently or not at all in Chromium).
    try { await navigator.storage?.persist?.(); } catch { /* optional */ }
}

/** All stored files as a JSON array of [path, base64]. */
export async function loadAll() {
    const store = db.transaction(STORE, 'readonly').objectStore(STORE);
    const [keys, values] = await Promise.all([request(store.getAllKeys()), request(store.getAll())]);
    return JSON.stringify(keys.map((k, i) => [k, values[i]]));
}

export function put(path, base64) {
    queue = queue.then(() => write(store => store.put(base64, path)))
        .catch(err => console.error(`[port] Storage: couldn't save ${path}:`, err));
}

export function remove(path) {
    queue = queue.then(() => write(store => store.delete(path)))
        .catch(err => console.error(`[port] Storage: couldn't delete ${path}:`, err));
}

/** Resolves once every queued write has been committed (used by tests / debugging). */
export function flushed() {
    return queue;
}

/** Debugging aid: `(await import('/js/stardewStorage.js')).debugList()` lists stored paths and sizes. */
export async function debugList() {
    const store = db.transaction(STORE, 'readonly').objectStore(STORE);
    const [keys, values] = await Promise.all([request(store.getAllKeys()), request(store.getAll())]);
    return keys.map((k, i) => `${k} (${Math.round(values[i].length * 0.75 / 1024)} KB)`);
}

function write(action) {
    return new Promise((resolve, reject) => {
        const tx = db.transaction(STORE, 'readwrite');
        action(tx.objectStore(STORE));
        tx.oncomplete = () => resolve();
        tx.onerror = () => reject(tx.error);
        tx.onabort = () => reject(tx.error);
    });
}
