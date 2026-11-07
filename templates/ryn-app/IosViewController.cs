#if IOS
using Microsoft.Extensions.DependencyInjection;
using Ryn.Ipc;
using Ryn.iOS;

namespace RynApp1;

public class IosViewController : RynViewController
{
    protected override void ConfigureServices(IServiceCollection services)
    {
        services.AddRynCommands();
        services.AddAppCommands();
    }
}
#endif
