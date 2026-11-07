using Microsoft.Extensions.DependencyInjection;
using Ryn.Ipc;
using Ryn.iOS;

namespace IosShowcase;

public class ViewController : RynViewController
{
    protected override void ConfigureServices(IServiceCollection services)
    {
        services.AddRynCommands();
        services.AddAppCommands();
    }
}
