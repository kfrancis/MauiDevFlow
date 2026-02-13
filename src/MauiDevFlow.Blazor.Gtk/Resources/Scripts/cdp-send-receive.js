(function() {
    if (typeof chobitsu === 'undefined') return JSON.stringify({error: 'chobitsu not loaded'});
    window.__cdpResponse = null;
    window.__cdpResponseReady = false;
    var orig = chobitsu.onMessage;
    chobitsu.setOnMessage(function(msg) {
        window.__cdpResponse = msg;
        window.__cdpResponseReady = true;
        chobitsu.setOnMessage(orig);
    });
    try {
        chobitsu.sendRawMessage('%CDP_MESSAGE%');
    } catch(e) {
        chobitsu.setOnMessage(orig);
        return JSON.stringify({error: e.message});
    }
    return '__cdp_pending__';
})();
