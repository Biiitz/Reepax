namespace Reepax.Services.Browser;

public static partial class BrowserSecurityGuard
{
    public static string GetAntiPopupScript() => """
        (function () {
            'use strict';
            const originalOpen = window.open;
            const originalAdd = EventTarget.prototype.addEventListener;
            const originalRemove = EventTarget.prototype.removeEventListener;
            const listeners = new WeakMap();
            const guardedEvents = new Set(['pointerdown', 'mousedown', 'mouseup', 'click', 'touchstart', 'touchend']);
            const controls = 'a[href], button, input[type="submit"], input[type="button"], input[type="image"], [role="button"]';
            const popupListeners = new WeakSet();
            const recoveredGestures = new WeakSet();
            const nativeBlockedGestures = new WeakMap();
            let activeListener = null;
            let forwardingClick = false;
            let overlayStyle = null;

            function recoverCoveredControl(event) {
                if (!event?.isTrusted || !(event.target instanceof Element) || event.target.closest(controls) ||
                    !Number.isFinite(event.clientX) || !Number.isFinite(event.clientY)) return;
                const hits = document.elementsFromPoint(event.clientX, event.clientY);
                const hit = hits.find(element => element.closest(controls));
                const control = hit?.closest(controls);
                if (!control || control.disabled || control.getAttribute('aria-disabled') === 'true') return;
                const overlays = [event.target, ...hits.slice(0, hits.indexOf(hit))].filter(element => {
                    if (element.contains(control) || element.children.length || element.textContent.trim() ||
                        element.getAttribute('role') || element.hasAttribute('tabindex') ||
                        !['DIV', 'SPAN'].includes(element.tagName)) return false;
                    const style = getComputedStyle(element);
                    return ['fixed', 'absolute'].includes(style.position) && Number(style.zIndex) >= 2147483000;
                });
                if (!overlays.includes(event.target)) return;
                if (!overlayStyle) {
                    overlayStyle = document.createElement('style');
                    overlayStyle.textContent = '[data-reepax-popup-overlay] { pointer-events: none !important; }';
                    (document.head || document.documentElement).appendChild(overlayStyle);
                }
                for (const overlay of overlays) overlay.setAttribute('data-reepax-popup-overlay', '');
                if (!['mousedown', 'click'].includes(event.type) || recoveredGestures.has(event)) return;
                recoveredGestures.add(event);
                setTimeout(() => {
                    if (!control.isConnected || control.disabled || event.defaultPrevented) return;
                    forwardingClick = true;
                    try { control.click(); } finally { forwardingClick = false; }
                }, 0);
            }

            function isGlobalTarget(target) {
                return target === window || target === document || target === document.body;
            }

            function findListener(target, type, listener, capture, create) {
                let types = listeners.get(target);
                if (!types && create) listeners.set(target, types = new Map());
                if (!types) return null;
                let callbacks = types.get(type);
                if (!callbacks && create) types.set(type, callbacks = new WeakMap());
                if (!callbacks) return null;
                let modes = callbacks.get(listener);
                if (!modes && create) callbacks.set(listener, modes = new Map());
                if (!modes) return null;
                if (!modes.has(capture) && create) {
                    modes.set(capture, function (event) {
                        if (forwardingClick && popupListeners.has(listener)) return;
                        const previous = activeListener;
                        activeListener = { event, listener, blocked: false };
                        try {
                            if (typeof listener === 'function') return Reflect.apply(listener, this, [event]);
                            return Reflect.apply(listener.handleEvent, listener, [event]);
                        } finally {
                            activeListener = previous;
                        }
                    });
                }
                return modes.get(capture) || null;
            }

            EventTarget.prototype.addEventListener = function (type, listener, options) {
                if (isGlobalTarget(this) && guardedEvents.has(type) && listener &&
                    (typeof listener === 'function' || typeof listener === 'object')) {
                    const capture = typeof options === 'boolean' ? options : !!options?.capture;
                    listener = findListener(this, type, listener, capture, true);
                }
                return Reflect.apply(originalAdd, this, [type, listener, options]);
            };

            EventTarget.prototype.removeEventListener = function (type, listener, options) {
                if (guardedEvents.has(type) && listener &&
                    (typeof listener === 'function' || typeof listener === 'object')) {
                    const capture = typeof options === 'boolean' ? options : !!options?.capture;
                    listener = findListener(this, type, listener, capture, false) || listener;
                }
                return Reflect.apply(originalRemove, this, [type, listener, options]);
            };

            window.open = function (url, target, features) {
                try {
                    const destination = url == null || url === '' ? 'about:blank' : new URL(url, document.baseURI).href;
                    const event = window.event || activeListener?.event;
                    const initiated = !!(event?.isTrusted || navigator.userActivation?.isActive);
                    const policy = window.chrome?.webview?.hostObjects?.sync?.reepaxPopupGuard;
                    if (policy && policy.ShouldBlockPopup(destination, initiated)) {
                        if (activeListener && activeListener.event === event) {
                            activeListener.blocked = true;
                            popupListeners.add(activeListener.listener);
                            recoverCoveredControl(event);
                        } else if (event?.isTrusted && isGlobalTarget(event.currentTarget) &&
                            ['pointerdown', 'mousedown', 'touchstart'].includes(event.type)) {
                            nativeBlockedGestures.set(event, event.currentTarget);
                            queueMicrotask(() => nativeBlockedGestures.delete(event));
                            recoverCoveredControl(event);
                        }
                        return null;
                    }
                } catch (_) {
                    // Native popup checks remain active when the bridge is unavailable.
                }
                return Reflect.apply(originalOpen, this, arguments);
            };

            for (const name of ['preventDefault', 'stopPropagation', 'stopImmediatePropagation']) {
                const original = Event.prototype[name];
                Event.prototype[name] = function () {
                    // Keep a denied popup's global handler from consuming the trusted control gesture.
                    const blocked = activeListener ? activeListener.blocked && activeListener.event === this :
                        nativeBlockedGestures.has(this) && nativeBlockedGestures.get(this) === this.currentTarget;
                    if (blocked && (this.isTrusted || forwardingClick) &&
                        this.target instanceof Element && (this.target.closest(controls) ||
                            this.target.hasAttribute('data-reepax-popup-overlay'))) {
                        return;
                    }
                    return Reflect.apply(original, this, arguments);
                };
            }
        })();
        """;
}
