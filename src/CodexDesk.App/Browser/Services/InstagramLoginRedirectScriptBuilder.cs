namespace CodexDesk.Services;

internal static class InstagramLoginRedirectScriptBuilder
{
    public static string Build()
    {
        return """
            (() => {
                if (window.__codexInstagramLoginRedirectFallback) {
                    return;
                }

                window.__codexInstagramLoginRedirectFallback = true;
                let redirectStarted = false;

                const followSuccessfulLogin = (body) => {
                    if (redirectStarted || window.location.hostname !== 'www.instagram.com') {
                        return;
                    }

                    try {
                        const login = JSON.parse(body)?.data?.caa_login_web;
                        if (login?.ig_authenticated !== true || typeof login.redirect_uri !== 'string') {
                            return;
                        }

                        const target = new URL(login.redirect_uri, window.location.href);
                        if (target.protocol !== 'https:' || target.hostname !== 'www.instagram.com' ||
                            !target.pathname.startsWith('/accounts/onetap')) {
                            return;
                        }

                        redirectStarted = true;
                        window.location.assign(target.href);
                    } catch {
                        // Ignore non-login and non-JSON XHR responses.
                    }
                };

                const originalOpen = XMLHttpRequest.prototype.open;
                XMLHttpRequest.prototype.open = function(method, url, ...args) {
                    this.__codexRequestUrl = typeof url === 'string' ? url : String(url ?? '');
                    return originalOpen.call(this, method, url, ...args);
                };

                const originalSend = XMLHttpRequest.prototype.send;
                XMLHttpRequest.prototype.send = function(...args) {
                    this.addEventListener('loadend', () => {
                        if (!this.__codexRequestUrl.includes('/api/graphql')) {
                            return;
                        }

                        try {
                            followSuccessfulLogin(this.responseText ?? '');
                        } catch {
                            // Access to responseText can be unavailable for non-text responses.
                        }
                    }, { once: true });

                    return originalSend.call(this, ...args);
                };
            })();
            """;
    }
}
