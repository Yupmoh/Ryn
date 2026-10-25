using Android.App;
using Android.Content.PM;
using Microsoft.Extensions.DependencyInjection;
using Ryn.Android;

namespace AndroidShowcase;

[Activity(
    Label = "Ryn Android Showcase",
    MainLauncher = true,
    Exported = true,
    Theme = "@android:style/Theme.NoTitleBar",
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize)]
public class MainActivity : RynActivity
{
    protected override void ConfigureServices(IServiceCollection services)
    {
        base.ConfigureServices(services);
        services.AddAppCommands();
    }
}
