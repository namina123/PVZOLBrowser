(function () {
    if (window.__ruffleBootstrapLoaded) {
        return;
    }
    window.__ruffleBootstrapLoaded = true;

    var ROOT = window.__pvzolRuffleRoot || "/__ruffle__/";
    var PROXY_ROOT = "/__proxy__/";
    var RUFFLE_SCRIPT_URL = ROOT + "ruffle.js";

    window.RufflePlayer = window.RufflePlayer || {};
    var existingConfig = window.RufflePlayer.config || {};
    window.RufflePlayer.config = {
        autoplay: "on",
        allowFullscreen: true,
        polyfills: true,
        publicPath: ROOT,
        unmuteOverlay: "hidden",
        warnOnUnsupportedContent: false
    };

    for (var configKey in existingConfig) {
        if (Object.prototype.hasOwnProperty.call(existingConfig, configKey)) {
            window.RufflePlayer.config[configKey] = existingConfig[configKey];
        }
    }

    var ruffleReady = false;
    var flashScanScheduled = false;
    var preflightedFlashUrls = {};
    var flashValidationStates = {};

    function stringifyForLog(value) {
        if (typeof value === "string") {
            return value;
        }

        try {
            return JSON.stringify(value);
        } catch (error) {
            try {
                return String(value);
            } catch (stringError) {
                return "[unserializable]";
            }
        }
    }

    function logInfo(message, details) {
        console.log("[pvzol-ruffle] " + message + (details === undefined ? "" : " " + stringifyForLog(details)));
    }

    function logError(message, details) {
        console.error("[pvzol-ruffle] " + message + (details === undefined ? "" : " " + stringifyForLog(details)));
    }

    function getFlashValidationState(url) {
        if (!url) {
            return null;
        }
        return flashValidationStates[url] || null;
    }

    function setFlashValidationState(url, status, details) {
        if (!url) {
            return;
        }
        flashValidationStates[url] = {
            status: status,
            details: details || null
        };
    }

    function isFlashResponseUsable(response) {
        if (!response) {
            return false;
        }

        var contentType = (response.headers.get("content-type") || "").toLowerCase();
        var contentLengthHeader = response.headers.get("content-length");
        var contentLength = contentLengthHeader === null || contentLengthHeader === ""
            ? null
            : parseInt(contentLengthHeader, 10);
        var hasExplicitZeroLength = contentLengthHeader !== null
            && !isNaN(contentLength)
            && contentLength <= 0;
        var looksLikeSwf = contentType.indexOf("application/x-shockwave-flash") !== -1
            || /\.swf(\?.*)?$/i.test(response.url || "");

        return response.ok && looksLikeSwf && !hasExplicitZeroLength;
    }

    function describeElement(element) {
        if (!element || !element.tagName) {
            return null;
        }

        return {
            tagName: element.tagName.toLowerCase(),
            id: element.id || "",
            className: typeof element.className === "string" ? element.className : "",
            src: element.getAttribute("src") || "",
            data: element.getAttribute("data") || "",
            width: element.getAttribute("width") || "",
            height: element.getAttribute("height") || "",
            originalUrl: element.getAttribute("data-pvzol-flash-url") || "",
            proxyUrl: element.getAttribute("data-pvzol-flash-proxy-url") || ""
        };
    }

    logInfo("bootstrap loaded", { href: window.location.href, readyState: document.readyState });

    function ensureViewportZoom() {
        var viewport = document.querySelector("meta[name='viewport']");
        var content = "width=device-width, initial-scale=1, minimum-scale=0.5, maximum-scale=5, user-scalable=yes";
        if (viewport) {
            viewport.setAttribute("content", content);
            return;
        }

        viewport = document.createElement("meta");
        viewport.name = "viewport";
        viewport.content = content;
        (document.head || document.documentElement).appendChild(viewport);
    }

    function injectCompatibilityStyle() {
        if (document.getElementById("__ruffle_compat_style__")) {
            return;
        }

        var style = document.createElement("style");
        style.id = "__ruffle_compat_style__";
        style.textContent =
            "html,body{max-width:100%;overflow:auto !important;}" +
            ".pvzol-ruffle-windowed-fullscreen{" +
            "position:fixed !important;" +
            "left:0 !important;" +
            "top:0 !important;" +
            "width:100vw !important;" +
            "height:100vh !important;" +
            "min-width:100vw !important;" +
            "max-width:100vw !important;" +
            "min-height:100vh !important;" +
            "max-height:100vh !important;" +
            "margin:0 !important;" +
            "z-index:2147483000 !important;" +
            "background:#000 !important;" +
            "}" +
            ".pvzol-ruffle-windowed-fullscreen ruffle-player," +
            ".pvzol-ruffle-windowed-fullscreen ruffle-embed," +
            ".pvzol-ruffle-windowed-fullscreen ruffle-object{" +
            "width:100% !important;" +
            "height:100% !important;" +
            "min-width:100% !important;" +
            "max-width:100% !important;" +
            "min-height:100% !important;" +
            "max-height:100% !important;" +
            "}" +
            "ruffle-player,ruffle-embed,ruffle-object,object,embed{" +
            "display:block !important;" +
            "margin:0 auto !important;" +
            "box-sizing:border-box !important;" +
            "}" +
            "ruffle-player canvas,ruffle-embed canvas,ruffle-object canvas{" +
            "image-rendering:auto;" +
            "}";
        (document.head || document.documentElement).appendChild(style);
    }

    function normalizeFlashLayout(root) {
        if (!root || !root.querySelectorAll) {
            return;
        }

        var elements = root.querySelectorAll("ruffle-player, ruffle-embed, ruffle-object, object, embed");
        for (var i = 0; i < elements.length; i += 1) {
            var element = elements[i];
            if (!element || !element.getAttribute) {
                continue;
            }

            var rawWidth = element.getAttribute("width");
            var rawHeight = element.getAttribute("height");
            var width = rawWidth ? parseFloat(String(rawWidth).replace(/[^\d.]/g, "")) : 0;
            var height = rawHeight ? parseFloat(String(rawHeight).replace(/[^\d.]/g, "")) : 0;

            if (width > 0 && height > 0) {
                element.style.width = String(Math.round(width)) + "px";
                element.style.minWidth = String(Math.round(width)) + "px";
                element.style.maxWidth = String(Math.round(width)) + "px";
                element.style.height = String(Math.round(height)) + "px";
                element.style.minHeight = String(Math.round(height)) + "px";
                element.style.maxHeight = String(Math.round(height)) + "px";
            }
        }
    }

    function toAbsoluteUrl(rawUrl) {
        if (!rawUrl) {
            return "";
        }

        try {
            return new URL(rawUrl, document.baseURI || window.location.href).toString();
        } catch (error) {
            return rawUrl;
        }
    }

    function toProxyUrl(rawUrl) {
        var absoluteUrl = toAbsoluteUrl(rawUrl);
        if (!absoluteUrl) {
            return "";
        }

        try {
            var parsed = new URL(absoluteUrl);
            var proxyBase = "";
            try {
                proxyBase = String(window.__pvzolProxyBase || "").replace(/\/+$/, "");
            } catch (proxyError) {
                proxyBase = "";
            }
            if (parsed.protocol !== "http:" && parsed.protocol !== "https:") {
                return absoluteUrl;
            }

            var shouldForceProxy = /\/pvz\/amf\/?(\?.*)?$/i.test(parsed.pathname + parsed.search);
            if (parsed.origin === window.location.origin) {
                if (shouldForceProxy) {
                    return (proxyBase || window.location.origin)
                        + PROXY_ROOT
                        + parsed.protocol.replace(":", "")
                        + "/"
                        + parsed.host
                        + parsed.pathname
                        + parsed.search;
                }
                return absoluteUrl;
            }

            if (parsed.pathname.indexOf(PROXY_ROOT) === 0 || parsed.pathname.indexOf(ROOT) === 0) {
                return absoluteUrl;
            }

            return window.location.origin
                + PROXY_ROOT
                + parsed.protocol.replace(":", "")
                + "/"
                + parsed.host
                + parsed.pathname
                + parsed.search;
        } catch (error) {
            return absoluteUrl;
        }
    }

    function cssDimensionValue(rawValue, fallbackPixels) {
        if (rawValue) {
            if (/^\d+(\.\d+)?$/.test(String(rawValue))) {
                return String(rawValue) + "px";
            }
            return String(rawValue);
        }
        if (fallbackPixels && fallbackPixels > 0) {
            return String(fallbackPixels) + "px";
        }
        return "";
    }

    function pixelDimensionValue(rawValue, fallbackPixels) {
        if (rawValue) {
            var text = String(rawValue).trim();
            if (/^\d+(\.\d+)?$/.test(text)) {
                return String(Math.round(parseFloat(text))) + "px";
            }
            var match = /^(\d+(?:\.\d+)?)px$/i.exec(text);
            if (match) {
                return String(Math.round(parseFloat(match[1]))) + "px";
            }
        }
        if (fallbackPixels && fallbackPixels > 0) {
            return String(Math.round(fallbackPixels)) + "px";
        }
        return "";
    }

    function extractFlashSourceUrl(element) {
        if (!element || !element.getAttribute) {
            return "";
        }

        var storedOriginal = element.getAttribute("data-pvzol-flash-url");
        if (storedOriginal) {
            return storedOriginal;
        }

        var directUrl = element.getAttribute("src") || element.getAttribute("data");
        if (directUrl) {
            return directUrl;
        }

        var movieParam = element.querySelector("param[name='movie'], param[name='src']");
        if (movieParam) {
            return movieParam.getAttribute("value") || "";
        }

        return "";
    }

    function applyFlashMetadata(element, originalUrl, proxyUrl) {
        if (!element || !element.setAttribute) {
            return;
        }

        if (originalUrl) {
            element.setAttribute("data-pvzol-flash-url", originalUrl);
        }
        if (proxyUrl) {
            element.setAttribute("data-pvzol-flash-proxy-url", proxyUrl);
        }
    }

    function isFlashElement(element) {
        if (!element || !element.tagName) {
            return false;
        }

        var tagName = element.tagName.toLowerCase();
        if (tagName !== "object" && tagName !== "embed") {
            return false;
        }

        var type = (element.getAttribute("type") || "").toLowerCase();
        var src = (
            element.getAttribute("src")
            || element.getAttribute("data")
            || ""
        ).toLowerCase();

        if (type.indexOf("application/x-shockwave-flash") !== -1) {
            return true;
        }

        if (src.indexOf(".swf") !== -1) {
            return true;
        }

        var movieParam = element.querySelector("param[name='movie'], param[name='src']");
        if (movieParam) {
            return movieParam.getAttribute("value").toLowerCase().indexOf(".swf") !== -1;
        }

        return false;
    }

    function rewriteFlashElementSource(element) {
        if (!isFlashElement(element)) {
            return;
        }

        var originalUrl = toAbsoluteUrl(extractFlashSourceUrl(element));
        var proxyUrl = toProxyUrl(originalUrl);
        var src = element.getAttribute("src");
        var data = element.getAttribute("data");
        applyFlashMetadata(element, originalUrl, proxyUrl);
        if (src) {
            element.setAttribute("src", toProxyUrl(src));
        }
        if (data) {
            element.setAttribute("data", toProxyUrl(data));
        }

        var params = element.querySelectorAll("param[name='movie'], param[name='src']");
        for (var i = 0; i < params.length; i += 1) {
            var currentValue = params[i].getAttribute("value");
            if (currentValue) {
                params[i].setAttribute("value", toProxyUrl(currentValue));
            }
        }

        logInfo("rewritten flash element", describeElement(element));
    }

    function rewriteExistingFlash(root) {
        if (!root || !root.querySelectorAll) {
            return;
        }

        var elements = root.querySelectorAll("object, embed");
        for (var i = 0; i < elements.length; i += 1) {
            rewriteFlashElementSource(elements[i]);
        }
    }

    function patchNetworkApis() {
        if (window.__pvzolRuffleNetworkPatched) {
            return;
        }
        window.__pvzolRuffleNetworkPatched = true;

        if (typeof window.fetch === "function") {
            var originalFetch = window.fetch.bind(window);
            window.fetch = function (input, init) {
                try {
                    var originalFetchUrl = "";
                    if (typeof input === "string") {
                        originalFetchUrl = input;
                        input = toProxyUrl(input);
                    } else if (input && typeof input.url === "string") {
                        originalFetchUrl = input.url;
                        input = new Request(toProxyUrl(input.url), input);
                    }
                    var effectiveFetchUrl = typeof input === "string"
                        ? input
                        : (input && typeof input.url === "string" ? input.url : "");
                    if (/\.swf(\?.*)?$/i.test(originalFetchUrl) || /\.swf(\?.*)?$/i.test(effectiveFetchUrl)) {
                        logInfo("fetch request", {
                            originalUrl: originalFetchUrl,
                            effectiveUrl: effectiveFetchUrl,
                            method: init && init.method ? init.method : "GET"
                        });
                    }
                } catch (error) {
                    logError("fetch rewrite failed", String(error));
                }
                return originalFetch(input, init);
            };
        }

        if (window.XMLHttpRequest && window.XMLHttpRequest.prototype) {
            var originalOpen = window.XMLHttpRequest.prototype.open;
            window.XMLHttpRequest.prototype.open = function (method, url) {
                var originalUrl = url;
                if (typeof url === "string") {
                    url = toProxyUrl(url);
                }
                if (typeof originalUrl === "string" && /\.swf(\?.*)?$/i.test(originalUrl)) {
                    logInfo("xhr open", {
                        method: method,
                        originalUrl: originalUrl,
                        effectiveUrl: url
                    });
                }
                return originalOpen.apply(this, [method, url].concat(Array.prototype.slice.call(arguments, 2)));
            };
        }

        if (navigator && typeof navigator.sendBeacon === "function") {
            var originalSendBeacon = navigator.sendBeacon.bind(navigator);
            navigator.sendBeacon = function (url, data) {
                if (typeof url === "string" && /\.swf(\?.*)?$/i.test(url)) {
                    logInfo("sendBeacon", { originalUrl: url, effectiveUrl: toProxyUrl(url) });
                }
                return originalSendBeacon(toProxyUrl(url), data);
            };
        }
    }

    function createReplacementHost(element, originalUrl, proxyUrl) {
        var host = document.createElement("div");
        host.className = "pvzol-ruffle-host";
        host.setAttribute("data-pvzol-flash-url", originalUrl);
        host.setAttribute("data-pvzol-flash-proxy-url", proxyUrl);
        host.setAttribute("data-pvzol-ruffle-managed", "1");
        host.style.display = "block";
        host.style.margin = "0 auto";
        host.style.boxSizing = "border-box";

        var rawStyle = element.getAttribute("style");
        if (rawStyle) {
            host.setAttribute("style", rawStyle + ";display:block;box-sizing:border-box;");
        }

        var width = cssDimensionValue(element.getAttribute("width"), element.clientWidth);
        var height = cssDimensionValue(element.getAttribute("height"), element.clientHeight);
        var fixedHeight = pixelDimensionValue(element.getAttribute("height"), element.clientHeight);
        if (width) {
            host.style.width = width;
            host.style.minWidth = width;
            host.style.maxWidth = width;
        }
        if (height) {
            host.style.height = fixedHeight || height;
            host.style.minHeight = fixedHeight || height;
            host.style.maxHeight = fixedHeight || height;
        } else if (element.clientHeight > 0) {
            host.style.height = String(element.clientHeight) + "px";
            host.style.minHeight = String(element.clientHeight) + "px";
            host.style.maxHeight = String(element.clientHeight) + "px";
        }
        if (!height && (!element.clientHeight || element.clientHeight < 32)) {
            host.style.height = "240px";
            host.style.minHeight = "240px";
            host.style.maxHeight = "240px";
        }
        return host;
    }

    function hidePlayerUnmuteOverlay(player) {
        if (!player || !player.shadowRoot) {
            return;
        }

        var overlay = player.shadowRoot.getElementById("unmute-overlay");
        if (!overlay) {
            return;
        }

        overlay.style.display = "none";
        overlay.style.opacity = "0";
        overlay.style.pointerEvents = "none";
        overlay.setAttribute("aria-hidden", "true");
    }

    function forcePlayerAudible(player, reason) {
        if (!player) {
            return;
        }

        try {
            player.volume = 1;
        } catch (error) {
            logError("unable to set player volume", { reason: reason, error: String(error) });
        }

        hidePlayerUnmuteOverlay(player);

        window.setTimeout(function () {
            hidePlayerUnmuteOverlay(player);
        }, 0);
        window.setTimeout(function () {
            hidePlayerUnmuteOverlay(player);
        }, 250);

        if (!player.__pvzolUnmuteObserverInstalled && player.shadowRoot && window.MutationObserver) {
            player.__pvzolUnmuteObserverInstalled = true;
            var observer = new MutationObserver(function () {
                hidePlayerUnmuteOverlay(player);
            });
            observer.observe(player.shadowRoot, {
                childList: true,
                subtree: true,
                attributes: true
            });
        }
    }

    function ensureRuffleFactory() {
        if (!window.RufflePlayer || typeof window.RufflePlayer.newest !== "function") {
            return null;
        }

        try {
            return window.RufflePlayer.newest();
        } catch (error) {
            logError("newest() failed", String(error));
            return null;
        }
    }

    function replaceFlashElementWithPlayer(element, reason) {
        if (!isFlashElement(element) || !element.parentNode) {
            return false;
        }

        if (element.getAttribute("data-pvzol-ruffle-managed") === "1") {
            return false;
        }

        var factory = ensureRuffleFactory();
        if (!factory || typeof factory.createPlayer !== "function") {
            logInfo("replace skipped because factory unavailable", {
                reason: reason,
                element: describeElement(element)
            });
            return false;
        }

        var originalUrl = toAbsoluteUrl(extractFlashSourceUrl(element));
        var proxyUrl = toProxyUrl(originalUrl);
        if (!/\.swf(\?.*)?$/i.test(proxyUrl)) {
            logInfo("replace skipped because source is not swf", {
                reason: reason,
                element: describeElement(element)
            });
            return false;
        }

        var validationState = getFlashValidationState(proxyUrl);
        if (!validationState || validationState.status === "pending") {
            logInfo("replace skipped because validation pending", {
                reason: reason,
                originalUrl: originalUrl,
                proxyUrl: proxyUrl
            });
            return false;
        }

        if (validationState.status !== "valid") {
            logInfo("replace skipped because validation failed", {
                reason: reason,
                originalUrl: originalUrl,
                proxyUrl: proxyUrl,
                validation: validationState
            });
            return false;
        }

        applyFlashMetadata(element, originalUrl, proxyUrl);
        element.setAttribute("data-pvzol-ruffle-managed", "1");

        var host = createReplacementHost(element, originalUrl, proxyUrl);
        try {
            var player = factory.createPlayer();
            player.style.width = host.style.width || element.style.width || "";
            player.style.height = host.style.height || element.style.height || "";
            player.style.display = "block";
            player.style.minWidth = host.style.minWidth || "";
            player.style.maxWidth = host.style.maxWidth || "";
            player.style.minHeight = host.style.minHeight || "";
            player.style.maxHeight = host.style.maxHeight || "";
            applyFlashMetadata(player, originalUrl, proxyUrl);
            forcePlayerAudible(player, "created");
            player.addEventListener("loadedmetadata", function () {
                forcePlayerAudible(player, "loadedmetadata");
            });
            player.addEventListener("loadeddata", function () {
                forcePlayerAudible(player, "loadeddata");
            });
            host.appendChild(player);
            element.parentNode.replaceChild(host, element);
            logInfo("player created", {
                reason: reason,
                originalUrl: originalUrl,
                proxyUrl: proxyUrl,
                host: describeElement(host)
            });

            var loadResult = player.load(proxyUrl);
            if (loadResult && typeof loadResult.then === "function") {
                loadResult.then(function () {
                    forcePlayerAudible(player, "load-resolved");
                    if (typeof player.play === "function") {
                        try {
                            player.play();
                        } catch (playError) {
                            logError("player play failed after load", {
                                reason: reason,
                                originalUrl: originalUrl,
                                proxyUrl: proxyUrl,
                                error: String(playError)
                            });
                        }
                    }
                    logInfo("player load resolved", {
                        reason: reason,
                        originalUrl: originalUrl,
                        proxyUrl: proxyUrl
                    });
                }).catch(function (error) {
                    logError("player load rejected", {
                        reason: reason,
                        originalUrl: originalUrl,
                        proxyUrl: proxyUrl,
                        error: String(error)
                    });
                });
            } else {
                forcePlayerAudible(player, "load-started");
                logInfo("player load started", {
                    reason: reason,
                    originalUrl: originalUrl,
                    proxyUrl: proxyUrl
                });
            }
            return true;
        } catch (error) {
            logError("replace failed", {
                reason: reason,
                originalUrl: originalUrl,
                proxyUrl: proxyUrl,
                error: String(error)
            });
            return false;
        }
    }

    function replaceFlashElements(root, reason) {
        if (!ruffleReady || !root || !root.querySelectorAll) {
            return 0;
        }

        var count = 0;
        var elements = root.querySelectorAll("object, embed");
        for (var i = 0; i < elements.length; i += 1) {
            if (replaceFlashElementWithPlayer(elements[i], reason)) {
                count += 1;
            }
        }
        if (count > 0) {
            logInfo("replaced flash elements", { reason: reason, count: count });
        }
        return count;
    }

    function observeFlashNodes() {
        if (!window.MutationObserver) {
            return;
        }

        var observer = new MutationObserver(function (mutations) {
            for (var i = 0; i < mutations.length; i += 1) {
                var addedNodes = mutations[i].addedNodes;
                for (var j = 0; j < addedNodes.length; j += 1) {
                    var node = addedNodes[j];
                    if (!node || node.nodeType !== Node.ELEMENT_NODE) {
                        continue;
                    }

                    if (isFlashElement(node)) {
                        rewriteFlashElementSource(node);
                        if (ruffleReady) {
                            replaceFlashElementWithPlayer(node, "mutation-node");
                        }
                        normalizeFlashLayout(node.parentNode || document);
                    } else {
                        rewriteExistingFlash(node);
                        if (ruffleReady) {
                            replaceFlashElements(node, "mutation-subtree");
                        }
                        normalizeFlashLayout(node);
                    }
                }
            }
        });

        observer.observe(document.documentElement || document, {
            childList: true,
            subtree: true
        });
    }

    function toggleWindowedFullscreen() {
        var host = document.querySelector(".pvzol-ruffle-host, .game-container");
        if (!host) {
            return "missing";
        }

        host.classList.toggle("pvzol-ruffle-windowed-fullscreen");
        return host.classList.contains("pvzol-ruffle-windowed-fullscreen") ? "enter" : "exit";
    }

    window.__pvzolToggleEmbeddedFullscreen = toggleWindowedFullscreen;

    function collectFlashProxyUrls(root) {
        var results = [];
        var seen = {};
        if (!root || !root.querySelectorAll) {
            return results;
        }

        var elements = root.querySelectorAll("object, embed");
        for (var i = 0; i < elements.length; i += 1) {
            var candidates = [
                elements[i].getAttribute("src"),
                elements[i].getAttribute("data")
            ];
            var params = elements[i].querySelectorAll("param[name='movie'], param[name='src']");
            for (var j = 0; j < params.length; j += 1) {
                candidates.push(params[j].getAttribute("value"));
            }
            for (var k = 0; k < candidates.length; k += 1) {
                var url = candidates[k];
                if (!url) {
                    continue;
                }
                var resolved = toProxyUrl(url);
                if (!/\.swf(\?.*)?$/i.test(resolved) || seen[resolved]) {
                    continue;
                }
                seen[resolved] = true;
                results.push(resolved);
            }
        }

        var managedNodes = root.querySelectorAll("[data-pvzol-flash-proxy-url]");
        for (var m = 0; m < managedNodes.length; m += 1) {
            var managedUrl = managedNodes[m].getAttribute("data-pvzol-flash-proxy-url");
            if (!managedUrl || seen[managedUrl]) {
                continue;
            }
            seen[managedUrl] = true;
            results.push(managedUrl);
        }

        return results;
    }

    function preflightFlashUrls(reason) {
        var urls = collectFlashProxyUrls(document);
        logInfo("preflight urls", { reason: reason, urls: urls });
        for (var i = 0; i < urls.length; i += 1) {
            if (preflightedFlashUrls[urls[i]]) {
                continue;
            }
            preflightedFlashUrls[urls[i]] = true;
            setFlashValidationState(urls[i], "pending", { reason: reason });
            (function (url) {
                fetch(url, { method: "HEAD" })
                    .then(function (response) {
                        var details = {
                            url: response.url || url,
                            status: response.status,
                            contentType: response.headers.get("content-type"),
                            contentLength: response.headers.get("content-length")
                        };
                        if (isFlashResponseUsable(response)) {
                            setFlashValidationState(url, "valid", details);
                            logInfo("preflight ok", details);
                            scheduleFlashRefresh("preflight-valid");
                            return;
                        }

                        setFlashValidationState(url, "invalid", details);
                        logError("preflight unusable swf", details);
                    })
                    .catch(function (error) {
                        setFlashValidationState(url, "invalid", { error: String(error) });
                        logError("preflight failed", String(error));
                    });
            })(urls[i]);
        }
    }

    function refreshFlashEmbedding(reason) {
        rewriteExistingFlash(document);
        preflightFlashUrls(reason);
        replaceFlashElements(document, reason);
        normalizeFlashLayout(document);
    }

    function scheduleFlashRefresh(reason) {
        window.__pvzolRuffleLastRefreshReason = reason;
        if (flashScanScheduled) {
            return;
        }
        flashScanScheduled = true;
        window.setTimeout(function () {
            flashScanScheduled = false;
            refreshFlashEmbedding(window.__pvzolRuffleLastRefreshReason || "scheduled");
        }, 0);
    }

    function installTouchBridge() {
        var hoverTarget = null;
        var hoverHost = null;
        var lastHoverX = 0;
        var lastHoverY = 0;
        var activeMouseTarget = null;
        var activeMouseHost = null;
        var nativeTouchBlocked = false;
        function bridgeLog(message, details) {
            return;
        }

        function describeTarget(target) {
            if (!target) {
                return "null";
            }

            var parts = [];
            parts.push(target.tagName ? target.tagName.toLowerCase() : String(target));
            if (target.id) {
                parts.push("#" + target.id);
            }
            if (target.className && typeof target.className === "string") {
                var className = target.className.trim().replace(/\s+/g, ".");
                if (className) {
                    parts.push("." + className);
                }
            }
            return parts.join("");
        }

        function buildPointCandidates(x, y) {
            var candidates = [];
            var dpr = window.devicePixelRatio || 1;
            var visualScale = window.visualViewport && window.visualViewport.scale
                ? window.visualViewport.scale
                : 1;
            var combinedScale = dpr * visualScale;

            function pushCandidate(label, px, py) {
                if (!isFinite(px) || !isFinite(py)) {
                    return;
                }
                for (var i = 0; i < candidates.length; i += 1) {
                    if (Math.abs(candidates[i].x - px) < 0.5 && Math.abs(candidates[i].y - py) < 0.5) {
                        return;
                    }
                }
                candidates.push({ label: label, x: px, y: py });
            }

            pushCandidate("raw", x, y);
            pushCandidate("dpr", x / dpr, y / dpr);
            pushCandidate("visual", x / visualScale, y / visualScale);
            pushCandidate("combined", x / combinedScale, y / combinedScale);
            return candidates;
        }

        function isRuffleHost(element) {
            if (!element || !element.tagName) {
                return false;
            }

            var tagName = element.tagName.toLowerCase();
            return tagName === "ruffle-player"
                || tagName === "ruffle-embed"
                || tagName === "ruffle-object";
        }

        function findRuffleHost(element) {
            if (!element) {
                return null;
            }
            if (isRuffleHost(element)) {
                return element;
            }
            if (element.closest) {
                var closestHost = element.closest("ruffle-player,ruffle-embed,ruffle-object");
                if (closestHost) {
                    return closestHost;
                }
            }
            var root = element.getRootNode ? element.getRootNode() : null;
            if (root && root.host && isRuffleHost(root.host)) {
                return root.host;
            }
            return null;
        }

        function isWithinRuffleSurface(element) {
            if (!element) {
                return false;
            }
            if (element.tagName && element.tagName.toLowerCase() === "canvas") {
                var canvasRoot = element.getRootNode ? element.getRootNode() : null;
                if (canvasRoot && canvasRoot.host && isRuffleHost(canvasRoot.host)) {
                    return true;
                }
            }
            return !!findRuffleHost(element);
        }

        function resolveDispatchTargetAt(x, y) {
            var candidates = buildPointCandidates(x, y);
            var element = null;
            var usedPoint = null;
            for (var i = 0; i < candidates.length; i += 1) {
                element = document.elementFromPoint(candidates[i].x, candidates[i].y);
                if (element) {
                    usedPoint = candidates[i];
                    break;
                }
            }

            if (!element) {
                bridgeLog("resolveDispatchTargetAt: no element", {
                    x: x,
                    y: y,
                    devicePixelRatio: window.devicePixelRatio || 1,
                    visualViewportScale: window.visualViewport && window.visualViewport.scale
                        ? window.visualViewport.scale
                        : 1,
                    candidates: candidates
                });
                return null;
            }

            if (element.tagName && element.tagName.toLowerCase() === "canvas") {
                bridgeLog("resolveDispatchTargetAt: canvas", {
                    requestedX: x,
                    requestedY: y,
                    resolvedX: usedPoint ? usedPoint.x : x,
                    resolvedY: usedPoint ? usedPoint.y : y,
                    candidate: usedPoint ? usedPoint.label : "raw",
                    target: describeTarget(element)
                });
                return {
                    target: element,
                    host: element,
                    eventX: usedPoint ? usedPoint.x : x,
                    eventY: usedPoint ? usedPoint.y : y
                };
            }

            var host = findRuffleHost(element);

            if (host && host.shadowRoot) {
                var shadowTarget = host.shadowRoot.querySelector("canvas") || host;
                bridgeLog("resolveDispatchTargetAt: shadow host", {
                    requestedX: x,
                    requestedY: y,
                    resolvedX: usedPoint ? usedPoint.x : x,
                    resolvedY: usedPoint ? usedPoint.y : y,
                    candidate: usedPoint ? usedPoint.label : "raw",
                    target: describeTarget(shadowTarget)
                });
                return {
                    target: shadowTarget,
                    host: host,
                    eventX: usedPoint ? usedPoint.x : x,
                    eventY: usedPoint ? usedPoint.y : y
                };
            }

            bridgeLog("resolveDispatchTargetAt: element", {
                requestedX: x,
                requestedY: y,
                resolvedX: usedPoint ? usedPoint.x : x,
                resolvedY: usedPoint ? usedPoint.y : y,
                candidate: usedPoint ? usedPoint.label : "raw",
                target: describeTarget(element)
            });
            return {
                target: element,
                host: element,
                eventX: usedPoint ? usedPoint.x : x,
                eventY: usedPoint ? usedPoint.y : y
            };
        }

        function dispatchPointer(target, type, x, y, button, buttons) {
            if (!target || !window.PointerEvent) {
                return false;
            }

            var event = new PointerEvent(type, {
                bubbles: true,
                cancelable: true,
                composed: true,
                clientX: x,
                clientY: y,
                pointerId: 1,
                pointerType: "mouse",
                isPrimary: true,
                button: button || 0,
                buttons: buttons || 0
            });
            return target.dispatchEvent(event);
        }

        function dispatchMouse(target, type, x, y, button, buttons, relatedTarget) {
            if (!target) {
                return false;
            }

            var event = new MouseEvent(type, {
                bubbles: true,
                cancelable: true,
                composed: true,
                clientX: x,
                clientY: y,
                button: button || 0,
                buttons: buttons || 0,
                relatedTarget: relatedTarget || null
            });
            return target.dispatchEvent(event);
        }

        function collectDispatchTargets(target, host) {
            var targets = [];

            function push(node) {
                if (!node) {
                    return;
                }
                for (var i = 0; i < targets.length; i += 1) {
                    if (targets[i] === node) {
                        return;
                    }
                }
                targets.push(node);
            }

            push(target);
            push(host);
            return targets;
        }

        function collectPrimaryTargets(target, host) {
            var targets = [];

            function push(node) {
                if (!node) {
                    return;
                }
                for (var i = 0; i < targets.length; i += 1) {
                    if (targets[i] === node) {
                        return;
                    }
                }
                targets.push(node);
            }

            push(host);
            push(target);
            return targets;
        }

        function describeTargets(targets) {
            var descriptions = [];
            for (var i = 0; i < targets.length; i += 1) {
                descriptions.push(describeTarget(targets[i]));
            }
            return descriptions;
        }

        function dispatchToTargets(targets, dispatcher) {
            var dispatched = false;
            for (var i = 0; i < targets.length; i += 1) {
                dispatched = dispatcher(targets[i]) || dispatched;
            }
            return dispatched;
        }

        function sameDispatchTargets(target, host) {
            return hoverTarget === target && hoverHost === host;
        }

        function dispatchHoverTransition(nextTarget, nextHost, x, y) {
            var previousTargets = collectDispatchTargets(hoverTarget, hoverHost);
            var nextTargets = collectDispatchTargets(nextTarget, nextHost);

            if (hoverTarget && !sameDispatchTargets(nextTarget, nextHost)) {
                dispatchToTargets(previousTargets, function (target) {
                    dispatchPointer(target, "pointerleave", lastHoverX, lastHoverY, 0, 0);
                    dispatchPointer(target, "pointerout", lastHoverX, lastHoverY, 0, 0);
                    dispatchMouse(target, "mouseout", lastHoverX, lastHoverY, 0, 0, nextTarget);
                    dispatchMouse(target, "mouseleave", lastHoverX, lastHoverY, 0, 0, nextTarget);
                    return true;
                });
            }

            if (!sameDispatchTargets(nextTarget, nextHost)) {
                dispatchToTargets(nextTargets, function (target) {
                    dispatchPointer(target, "pointerenter", x, y, 0, 0);
                    dispatchPointer(target, "pointerover", x, y, 0, 0);
                    dispatchMouse(target, "mouseover", x, y, 0, 0, hoverTarget);
                    dispatchMouse(target, "mouseenter", x, y, 0, 0, hoverTarget);
                    return true;
                });
            }
        }

        function dispatchContextMenu(targets, x, y) {
            if (!targets || !targets.length) {
                return false;
            }

            return dispatchToTargets(targets, function (target) {
                var event = new MouseEvent("contextmenu", {
                    bubbles: true,
                    cancelable: true,
                    composed: true,
                    clientX: x,
                    clientY: y,
                    button: 2,
                    buttons: 2
                });
                return target.dispatchEvent(event);
            });
        }

        function focusTargets(targets) {
            for (var i = 0; i < targets.length; i += 1) {
                var target = targets[i];
                if (target && typeof target.focus === "function") {
                    try {
                        target.focus({ preventScroll: true });
                    } catch (error) {
                        try {
                            target.focus();
                        } catch (ignored) {
                        }
                    }
                }
            }
        }

        function dispatchPressSequence(targets, x, y) {
            return dispatchToTargets(targets, function (target) {
                dispatchPointer(target, "pointerdown", x, y, 0, 1);
                dispatchMouse(target, "mousedown", x, y, 0, 1, null);
                return true;
            });
        }

        function dispatchReleaseSequence(targets, x, y) {
            return dispatchToTargets(targets, function (target) {
                dispatchPointer(target, "pointerup", x, y, 0, 0);
                dispatchMouse(target, "mouseup", x, y, 0, 0, null);
                dispatchMouse(target, "click", x, y, 0, 0, null);
                if (typeof target.click === "function") {
                    try {
                        target.click();
                    } catch (error) {
                    }
                }
                return true;
            });
        }

        function installNativeTouchBlockers() {
            if (window.__ruffleWrapperTouchBlockersInstalled) {
                return;
            }
            window.__ruffleWrapperTouchBlockersInstalled = true;

            function getBlockedTarget(event) {
                if (!event) {
                    return null;
                }
                var path = event.composedPath ? event.composedPath() : null;
                if (path && path.length) {
                    for (var i = 0; i < path.length; i += 1) {
                        if (path[i] && path[i].nodeType === Node.ELEMENT_NODE && isWithinRuffleSurface(path[i])) {
                            return path[i];
                        }
                    }
                }
                return event.target && event.target.nodeType === Node.ELEMENT_NODE && isWithinRuffleSurface(event.target)
                    ? event.target
                    : null;
            }

            function blockTouchEvent(event) {
                if (!nativeTouchBlocked) {
                    return;
                }
                var target = getBlockedTarget(event);
                if (!target) {
                    return;
                }
                event.preventDefault();
                event.stopImmediatePropagation();
                bridgeLog("blocked native touch", {
                    type: event.type,
                    target: describeTarget(target)
                });
            }

            function blockTouchPointerEvent(event) {
                if (!nativeTouchBlocked) {
                    return;
                }
                if (!event || event.pointerType !== "touch") {
                    return;
                }
                var target = getBlockedTarget(event);
                if (!target) {
                    return;
                }
                event.preventDefault();
                event.stopImmediatePropagation();
                bridgeLog("blocked native pointer", {
                    type: event.type,
                    target: describeTarget(target)
                });
            }

            document.addEventListener("touchstart", blockTouchEvent, { capture: true, passive: false });
            document.addEventListener("touchmove", blockTouchEvent, { capture: true, passive: false });
            document.addEventListener("touchend", blockTouchEvent, { capture: true, passive: false });
            document.addEventListener("touchcancel", blockTouchEvent, { capture: true, passive: false });
            document.addEventListener("pointerdown", blockTouchPointerEvent, { capture: true, passive: false });
            document.addEventListener("pointermove", blockTouchPointerEvent, { capture: true, passive: false });
            document.addEventListener("pointerup", blockTouchPointerEvent, { capture: true, passive: false });
            document.addEventListener("pointercancel", blockTouchPointerEvent, { capture: true, passive: false });
        }

        installNativeTouchBlockers();
        window.__ruffleWrapperTouchBridge = {
            hoverAt: function (x, y) {
                var hit = resolveDispatchTargetAt(x, y);
                if (!hit || !hit.target) {
                    bridgeLog("hoverAt failed", { x: x, y: y });
                    return false;
                }

                bridgeLog("hoverAt", {
                    x: x,
                    y: y,
                    eventX: hit.eventX,
                    eventY: hit.eventY,
                    target: describeTarget(hit.target),
                    host: describeTarget(hit.host),
                    previousHover: describeTarget(hoverTarget)
                });
                dispatchHoverTransition(hit.target, hit.host, hit.eventX, hit.eventY);
                dispatchToTargets(collectDispatchTargets(hit.target, hit.host), function (target) {
                    dispatchPointer(target, "pointermove", hit.eventX, hit.eventY, 0, 0);
                    dispatchMouse(target, "mousemove", hit.eventX, hit.eventY, 0, 0, null);
                    return true;
                });
                hoverTarget = hit.target;
                hoverHost = hit.host;
                lastHoverX = hit.eventX;
                lastHoverY = hit.eventY;
                return true;
            },
            leaveHover: function () {
                if (!hoverTarget) {
                    bridgeLog("leaveHover ignored");
                    return false;
                }

                bridgeLog("leaveHover", {
                    target: describeTarget(hoverTarget),
                    x: lastHoverX,
                    y: lastHoverY
                });
                dispatchToTargets(collectDispatchTargets(hoverTarget, hoverHost), function (target) {
                    dispatchPointer(target, "pointerleave", lastHoverX, lastHoverY, 0, 0);
                    dispatchPointer(target, "pointerout", lastHoverX, lastHoverY, 0, 0);
                    dispatchMouse(target, "mouseout", lastHoverX, lastHoverY, 0, 0, null);
                    dispatchMouse(target, "mouseleave", lastHoverX, lastHoverY, 0, 0, null);
                    return true;
                });
                hoverTarget = null;
                hoverHost = null;
                activeMouseTarget = null;
                return true;
            },
            clickAt: function (x, y) {
                var hit = resolveDispatchTargetAt(x, y);
                if (!hit || !hit.target) {
                    bridgeLog("clickAt failed", { x: x, y: y });
                    return false;
                }

                bridgeLog("clickAt", {
                    x: x,
                    y: y,
                    eventX: hit.eventX,
                    eventY: hit.eventY,
                    target: describeTarget(hit.target),
                    host: describeTarget(hit.host)
                });
                this.hoverAt(x, y);
                var targets = collectPrimaryTargets(hit.target, hit.host);
                bridgeLog("clickAt dispatchTargets", describeTargets(targets));
                focusTargets(targets);
                dispatchPressSequence(targets, hit.eventX, hit.eventY);
                activeMouseTarget = hit.target;
                activeMouseHost = hit.host;
                window.requestAnimationFrame(function () {
                    dispatchReleaseSequence(targets, hit.eventX, hit.eventY);
                    activeMouseTarget = null;
                    activeMouseHost = null;
                });
                return true;
            },
            releaseAt: function (x, y) {
                var hit = resolveDispatchTargetAt(x, y);
                var target = hit ? hit.target : hoverTarget;
                var host = hit ? hit.host : hoverHost;
                var eventX = hit ? hit.eventX : x;
                var eventY = hit ? hit.eventY : y;
                if (!target) {
                    bridgeLog("releaseAt failed", { x: x, y: y });
                    return false;
                }

                bridgeLog("releaseAt", {
                    x: x,
                    y: y,
                    eventX: eventX,
                    eventY: eventY,
                    target: describeTarget(target),
                    host: describeTarget(host),
                    hoverTarget: describeTarget(hoverTarget),
                    activeMouseTarget: describeTarget(activeMouseTarget)
                });
                this.hoverAt(x, y);
                var targets = collectPrimaryTargets(target, host);
                bridgeLog("releaseAt dispatchTargets", describeTargets(targets));
                focusTargets(targets);
                dispatchPressSequence(targets, eventX, eventY);
                activeMouseTarget = target;
                activeMouseHost = host;

                window.requestAnimationFrame(function () {
                    dispatchReleaseSequence(targets, eventX, eventY);
                });

                window.setTimeout(function () {
                    dispatchToTargets(targets, function (dispatchTarget) {
                        dispatchPointer(dispatchTarget, "pointerleave", eventX, eventY, 0, 0);
                        dispatchPointer(dispatchTarget, "pointerout", eventX, eventY, 0, 0);
                        dispatchMouse(dispatchTarget, "mouseout", eventX, eventY, 0, 0, null);
                        dispatchMouse(dispatchTarget, "mouseleave", eventX, eventY, 0, 0, null);
                        return true;
                    });

                    hoverTarget = null;
                    hoverHost = null;
                    activeMouseTarget = null;
                    activeMouseHost = null;
                    lastHoverX = eventX;
                    lastHoverY = eventY;
                    bridgeLog("releaseAt completed", {
                        x: x,
                        y: y,
                        eventX: eventX,
                        eventY: eventY,
                        target: describeTarget(target),
                        host: describeTarget(host)
                    });
                }, 16);
                return true;
            },
            contextMenuAt: function (x, y) {
                var hit = resolveDispatchTargetAt(x, y);
                if (!hit || !hit.target) {
                    bridgeLog("contextMenuAt failed", { x: x, y: y });
                    return false;
                }

                bridgeLog("contextMenuAt", {
                    x: x,
                    y: y,
                    eventX: hit.eventX,
                    eventY: hit.eventY,
                    target: describeTarget(hit.target),
                    host: describeTarget(hit.host)
                });
                this.hoverAt(x, y);
                dispatchContextMenu(collectDispatchTargets(hit.target, hit.host), hit.eventX, hit.eventY);
                return true;
            },
            setNativeTouchBlocked: function (blocked) {
                nativeTouchBlocked = !!blocked;
                bridgeLog("setNativeTouchBlocked", { blocked: nativeTouchBlocked });
                return true;
            }
        };
    }

    function onRuffleReady() {
        ruffleReady = true;
        logInfo("onRuffleReady", { readyState: document.readyState });
        scheduleFlashRefresh("ruffle-ready");
        window.setTimeout(function () {
            scheduleFlashRefresh("ruffle-ready-delayed");
        }, 200);
    }

    function loadRuffle() {
        logInfo("loadRuffle start", { readyState: document.readyState });
        ensureViewportZoom();
        injectCompatibilityStyle();
        patchNetworkApis();
        installTouchBridge();
        observeFlashNodes();
        scheduleFlashRefresh("bootstrap-load");

        if (window.RufflePlayer && typeof window.RufflePlayer.newest === "function") {
            logInfo("runtime already present");
            onRuffleReady();
            return;
        }

        var existingScript = document.querySelector("script[data-ruffle-loader='1']");
        if (existingScript) {
            logInfo("reusing existing runtime script");
            existingScript.addEventListener("load", onRuffleReady, { once: true });
            return;
        }

        var script = document.createElement("script");
        script.src = RUFFLE_SCRIPT_URL;
        script.async = false;
        script.dataset.ruffleLoader = "1";
        script.onload = onRuffleReady;
        script.onerror = function (error) {
            logError("Unable to load Ruffle runtime", String(error));
        };

        logInfo("requesting runtime", { url: RUFFLE_SCRIPT_URL });

        (document.head || document.documentElement).appendChild(script);
    }

    window.addEventListener("resize", function () {
        normalizeFlashLayout(document);
    });
    document.addEventListener("DOMContentLoaded", function () {
        scheduleFlashRefresh("dom-content-loaded");
    }, { once: true });

    loadRuffle();
})();
