namespace InterceptKit.WebView.Windows;

using InterceptKit.Core;
using System.Text;
using System.Text.Json;

public static class WebView2InterceptPayloadBuilder
{
    public static string BuildContinueRequestPayload(
        string requestId,
        EditableInterceptedRequest? editedRequest,
        bool interceptResponse)
    {
        if (editedRequest is null)
        {
            return JsonSerializer.Serialize(new
            {
                requestId,
                interceptResponse
            });
        }

        return JsonSerializer.Serialize(new
        {
            requestId,
            url = editedRequest.Url,
            method = editedRequest.Method,
            headers = editedRequest.Headers.Select(header => new
            {
                name = header.Key,
                value = header.Value
            }),
            postData = editedRequest.Body is null
                ? null
                : Convert.ToBase64String(Encoding.UTF8.GetBytes(editedRequest.Body)),
            interceptResponse
        });
    }

    public static string BuildFailRequestPayload(string requestId)
    {
        return JsonSerializer.Serialize(new
        {
            requestId,
            errorReason = "Aborted"
        });
    }
}
