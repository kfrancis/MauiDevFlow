// Blazor JS initializer for MauiDevFlow.Blazor
// Automatically injects chobitsu.js script tag before Blazor starts

export function beforeStart(options, extensions) {
    // Check if chobitsu is already loaded (e.g. manual script tag)
    if (typeof chobitsu !== 'undefined') {
        return;
    }

    // Check if script tag already exists
    if (document.querySelector('script[src*="chobitsu"]')) {
        return;
    }

    // Inject chobitsu.js script tag (fire-and-forget — C# side polls for availability)
    const script = document.createElement('script');
    script.src = 'chobitsu.js';
    script.async = false;
    document.head.appendChild(script);
}
