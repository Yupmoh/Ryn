#if ANDROID
using Android.App;
using Microsoft.Extensions.DependencyInjection;
using Ryn.Android;
using Ryn.Ipc;

namespace RynApp1;

[Activity(
    Label = "RynApp1",
    Theme = "@android:style/Theme.NoTitleBar",
    MainLauncher = true,
    Exported = true)]
public class MainActivity : RynActivity
{
    protected override void ConfigureServices(IServiceCollection services)
    {
        services.AddRynCommands();
        services.AddAppCommands();
    }
}
#endif
