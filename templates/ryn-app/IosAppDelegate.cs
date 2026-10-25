#if IOS
using Foundation;
using UIKit;

namespace RynApp1;

[Register("AppDelegate")]
public class IosAppDelegate : UIApplicationDelegate
{
    public override UIWindow? Window { get; set; }

    public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
    {
        Window = new UIWindow(UIScreen.MainScreen.Bounds);
        Window.RootViewController = new IosViewController();
        Window.MakeKeyAndVisible();
        return true;
    }
}
#endif
