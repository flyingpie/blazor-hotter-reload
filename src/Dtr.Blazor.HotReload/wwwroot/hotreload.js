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
