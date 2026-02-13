(function() {
    // Prevent double-injection
    if (window.__chobitsuDebugEnabled) {
        console.log('[ChobitsuDebug] Already initialized');
        return 'already_initialized';
    }
    window.__chobitsuDebugEnabled = true;
    
    if (typeof chobitsu === 'undefined') {
        console.error('[ChobitsuDebug] chobitsu not found');
        return 'chobitsu_not_found';
    }
    
    console.log('[ChobitsuDebug] Chobitsu initialized for single-eval CDP.');
    window.__chobitsuReady = true;
    return 'ready';
})();
