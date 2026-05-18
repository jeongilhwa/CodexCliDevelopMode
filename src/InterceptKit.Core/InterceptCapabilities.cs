namespace InterceptKit.Core;

[Flags]
public enum InterceptCapabilities
{
    None = 0,
    EditHeaders = 1 << 0,
    EditBody = 1 << 1,
    CaptureResponse = 1 << 2
}
