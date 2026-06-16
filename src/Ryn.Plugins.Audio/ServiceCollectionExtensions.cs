using Microsoft.Extensions.DependencyInjection;
using Ryn.Core;

namespace Ryn.Plugins.Audio;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddRynAudio(this IServiceCollection services)
    {
        services.AddSingleton(sp => new AudioService(sp.GetRequiredService<IMainThreadDispatcher>()));
        services.AddSingleton<AudioCommands>();
        services.AddSingleton<IRynPlugin, AudioPlugin>();
        services.AddAudioCommands();

        return services;
    }
}
