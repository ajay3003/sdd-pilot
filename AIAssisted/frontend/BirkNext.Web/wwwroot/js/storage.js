window.birkNextStorage = {
    getItem: (key) => localStorage.getItem(key),
    setItem: (key, value) => localStorage.setItem(key, value),
    removeItem: (key) => localStorage.removeItem(key),
    // Detection snapshots are safe public discovery evidence, persisted separately from Target Environment configuration so that
    // running Detect never modifies or "saves" the saved profile. No credential is ever stored here.
    getSnapshots: () => localStorage.getItem('birknext:detection-snapshots'),
    setSnapshots: (value) => localStorage.setItem('birknext:detection-snapshots', value),
};
