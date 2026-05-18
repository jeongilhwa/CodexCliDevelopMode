namespace InterceptKit.Core;

public sealed record InterceptedRequest(
    string RequestId,
    string Method,
    string Url,
    IReadOnlyDictionary<string, string> Headers,
    string? Body,
    DateTimeOffset CapturedAt);

public sealed record EditableInterceptedRequest(
    string Method,
    string Url,
    IReadOnlyDictionary<string, string> Headers,
    string? Body);

public sealed record InterceptedResponse(
    int StatusCode,
    string? ReasonPhrase,
    IReadOnlyDictionary<string, string> Headers,
    string? Body,
    DateTimeOffset CapturedAt);

public enum InterceptDecisionKind
{
    ForwardOriginal,
    ForwardModified,
    Drop
}

public sealed record InterceptDecision(
    InterceptDecisionKind Kind,
    EditableInterceptedRequest? EditedRequest)
{
    public static InterceptDecision ForwardOriginal()
    {
        return new(InterceptDecisionKind.ForwardOriginal, null);
    }

    public static InterceptDecision ForwardModified(EditableInterceptedRequest request)
    {
        return new(InterceptDecisionKind.ForwardModified, request);
    }

    public static InterceptDecision Drop()
    {
        return new(InterceptDecisionKind.Drop, null);
    }
}

public enum InterceptSessionStatus
{
    Pending,
    Forwarded,
    Dropped,
    Completed,
    Failed
}

public sealed record InterceptSession(
    Guid Id,
    InterceptedRequest OriginalRequest,
    EditableInterceptedRequest? EditedRequest,
    InterceptDecision Decision,
    InterceptSessionStatus Status,
    InterceptedResponse? Response,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ResolvedAt);
