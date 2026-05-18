namespace InterceptKit.Presentation;

public sealed record InterceptQueueItemViewModel(
    Guid SessionId,
    string Method,
    string Url,
    string Status);
