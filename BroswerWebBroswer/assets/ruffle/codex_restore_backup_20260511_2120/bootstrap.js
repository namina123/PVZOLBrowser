(function () {
    if (window.__ruffleBootstrapLoaded) {
        return;
    }
    window.__ruffleBootstrapLoaded = true;

    var ROOT = window.__pvzolRuffleRoot || "/__ruffle__/";
    var PROXY_ROOT = "/__proxy__/";
    var RUFFLE_SCRIPT_URL = ROOT + "ruffle.js";
    var TOUCH_BRIDGE_ENABLED = window.__pvzolTouchBridgeEnabled !== false;

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
            "ruffle-player,ruffle-embed,ruffle-object,object,embed{" +
            "display:block !important;" +
            "margin:0 auto !important;" +
            "max-width:100% !important;" +
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
                var desiredWidth = "min(100%, " + width + "px)";
                if (element.style.width !== desiredWidth) {
                    element.style.width = desiredWidth;
                }
                if (element.style.maxHeight !== "100%") {
                    element.style.maxHeight = "100%";
                }
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

    function hideAllUnmuteOverlays() {
        var players = document.querySelectorAll("ruffle-player,ruffle-embed,ruffle-object");
        for (var i = 0; i < players.length; i += 1) {
            var player = players[i];
            if (!player || !player.shadowRoot) {
                continue;
            }

            var overlay = player.shadowRoot.getElementById("unmute-overlay");
            if (!overlay) {
                continue;
            }

            overlay.style.display = "none";
            overlay.style.opacity = "0";
            overlay.style.pointerEvents = "none";
            overlay.setAttribute("aria-hidden", "true");
        }
    }

    function toProxyUrl(rawUrl) {
        var absoluteUrl = toAbsoluteUrl(rawUrl);
        if (!absoluteUrl) {
            return "";
        }

        try {
            var parsed = new URL(absoluteUrl);
            if (parsed.protocol !== "http:" && parsed.protocol !== "https:") {
                return absoluteUrl;
            }

            if (parsed.origin === window.location.origin) {
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

        var src = element.getAttribute("src");
        var data = element.getAttribute("data");
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
                        normalizeFlashLayout(node.parentNode || document);
                    } else {
                        rewriteExistingFlash(node);
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

    function installTouchBridge() {
        if (!TOUCH_BRIDGE_ENABLED) {
            try {
                console.log("[pvzol-ruffle-bootstrap]", JSON.stringify({
                    touchBridgeEnabled: false
                }));
            } catch (error) {
            }
            return;
        }

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
        if (window.RufflePlayer && typeof window.RufflePlayer.polyfill === "function") {
            try {
                window.RufflePlayer.polyfill();
                try {
                    console.log("[pvzol-ruffle-ready]", JSON.stringify({
                        preferredRenderer: window.RufflePlayer && window.RufflePlayer.config
                            ? window.RufflePlayer.config.preferredRenderer
                            : "",
                        touchBridgeEnabled: TOUCH_BRIDGE_ENABLED
                    }));
                } catch (logError) {
                }
                hideAllUnmuteOverlays();
                window.requestAnimationFrame(function () {
                    normalizeFlashLayout(document);
                    hideAllUnmuteOverlays();
                });
                window.setTimeout(function () {
                    normalizeFlashLayout(document);
                    hideAllUnmuteOverlays();
                }, 180);
            } catch (error) {
                console.error("Ruffle polyfill failed:", error);
            }
        }
    }

    function loadRuffle() {
        ensureViewportZoom();
        injectCompatibilityStyle();
        installTouchBridge();
        rewriteExistingFlash(document);
        normalizeFlashLayout(document);
        observeFlashNodes();

        if (window.RufflePlayer && typeof window.RufflePlayer.newest === "function") {
            onRuffleReady();
            return;
        }

        var existingScript = document.querySelector("script[data-ruffle-loader='1']");
        if (existingScript) {
            existingScript.addEventListener("load", onRuffleReady, { once: true });
            return;
        }

        var script = document.createElement("script");
        script.src = RUFFLE_SCRIPT_URL;
        script.async = false;
        script.dataset.ruffleLoader = "1";
        script.onload = onRuffleReady;
        script.onerror = function (error) {
            console.error("Unable to load Ruffle runtime:", error);
        };

        (document.head || document.documentElement).appendChild(script);
    }

    loadRuffle();
})();
