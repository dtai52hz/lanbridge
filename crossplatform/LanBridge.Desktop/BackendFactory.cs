using LanBridge.CrossPlatform;

namespace LanBridge.Desktop;

internal static class BackendFactory
{
    internal static IInputBackend Create() => OperatingSystem.IsLinux()
        ? new LinuxInputBackend() : new SharpHookInputBackend();
}
