namespace Ryn.iOS;

public static class RynIosPlatform
{
    public static bool IsSupported => OperatingSystem.IsIOS();
}
