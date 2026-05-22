namespace CodexDesk.Services;

using System.Text.Encodings.Web;
using System.Text.Json;

internal static class WebViewFetchRequestScriptBuilder
{
    private static readonly JsonSerializerOptions ScriptJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string Build(string url, string method, string? body)
    {
        var request = new
        {
            url = string.IsNullOrWhiteSpace(url) ? "/" : url.Trim(),
            method = string.IsNullOrWhiteSpace(method) ? "GET" : method.Trim().ToUpperInvariant(),
            body = body?.Trim() ?? string.Empty
        };

        var requestJson = JsonSerializer.Serialize(request, ScriptJsonOptions);

        return $$"""
(() => {
    const request = {{requestJson}};
    const url = request.url;
    const method = request.method;
    const body = request.body ?? '';
    const options = {
        method,
        credentials: url.startsWith('http') ? 'include' : 'same-origin',
        headers: {
            Accept: 'application/json, text/plain, */*'
        }
    };

    if (body.length > 0) {
        options.headers = {
            ...options.headers,
            'Content-Type': 'application/json'
        };
        options.body = body;
    }

    return fetch(url, options)
        .then(async response => {
            const responseBody = await response.text();
            return JSON.stringify({
                ok: response.ok,
                status: response.status,
                statusText: response.statusText,
                url: response.url,
                body: responseBody
            });
        })
        .catch(error => JSON.stringify({
            ok: false,
            status: 0,
            statusText: error?.message ?? String(error),
            url,
            body: ''
        }));
})();
""";
    }
}
