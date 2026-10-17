// Reloads the page, unless it was already reloaded for the same reason (key) in this browser tab. Returns whether it reloads.
// sessionStorage is per tab and survives the reload, which is what prevents a reload loop (a reloaded page starts over, but
// sees the marker and doesn't reload again for the same key).
export function reloadOnce(key) {
	const storageKey = "dtr-hotreload:" + key;
	if (sessionStorage.getItem(storageKey)) {
		return false;
	}

	sessionStorage.setItem(storageKey, "1");
	location.reload();
	return true;
}

// Re-fetches the stylesheet(s) matching the given path (relative to wwwroot), by cache-busting their URL.
// Without a path, all stylesheets of the app's origin are reloaded.
export function reloadCss(path) {
	const suffix = "/" + path.replace(/^\/+/, "");
	for (const link of document.querySelectorAll("link[rel='stylesheet'], link[rel='preload'][as='style']")) {
		const url = new URL(link.href, document.baseURI);
		if (url.origin !== location.origin || (path && !url.pathname.endsWith(suffix))) {
			continue;
		}

		// The query string makes the browser fetch the file again instead of using its cache. A preload link is turned into a
		// real stylesheet, so it also applies the new CSS (not just fetches it).
		url.searchParams.set("hotreload", Date.now().toString());
		link.rel = "stylesheet";
		link.href = url.href;
		console.log("[hotreload] Reloaded " + url.pathname);
	}
}
