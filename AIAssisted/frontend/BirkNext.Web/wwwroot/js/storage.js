window.birkNextStorage = {
    getItem: (key) => localStorage.getItem(key),
    setItem: (key, value) => localStorage.setItem(key, value),
    removeItem: (key) => localStorage.removeItem(key),
    // Detection snapshots are safe public discovery evidence, persisted separately from Target Environment configuration so that
    // running Detect never modifies or "saves" the saved profile. No credential is ever stored here.
    getSnapshots: () => localStorage.getItem('birknext:detection-snapshots'),
    setSnapshots: (value) => localStorage.setItem('birknext:detection-snapshots', value),
    // Endpoint Discovery is safe page-oriented network metadata (no token, cookie, body or query string), persisted separately per
    // Target Environment so restart restores discovery history without ever restoring a live credential.
    getDiscovery: () => localStorage.getItem('birknext:endpoint-discovery'),
    setDiscovery: (value) => localStorage.setItem('birknext:endpoint-discovery', value),
    // Local data reset: removes the named project keys and every key starting with one of the prefixes. Returns the removed keys.
    removeProjectState: (keys, prefixes) => {
        const removed = [];
        for (const key of keys) { if (localStorage.getItem(key) !== null) { localStorage.removeItem(key); removed.push(key); } }
        for (let i = localStorage.length - 1; i >= 0; i--) {
            const key = localStorage.key(i);
            if (key && prefixes.some(p => key.startsWith(p))) { localStorage.removeItem(key); removed.push(key); }
        }
        return removed;
    },
};
