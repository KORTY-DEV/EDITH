namespace Edith.Audio;

public sealed record AudioInputDeviceInfo(
    string Id,
    string Name,
    bool IsDefaultConsole,
    bool IsDefaultCommunications);
