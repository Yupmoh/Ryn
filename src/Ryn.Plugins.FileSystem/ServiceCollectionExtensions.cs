using Microsoft.Extensions.DependencyInjection;
using Ryn.Ipc;

namespace Ryn.Plugins.FileSystem;

public static class FileSystemServiceCollectionExtensions
{
    public static IServiceCollection AddRynFileSystem(this IServiceCollection services, Action<FileSystemOptions>? configure = null)
    {
        var options = new FileSystemOptions();
        configure?.Invoke(options);
        services.AddSingleton(sp =>
        {
            var caps = sp.GetService<RynCapabilities>();
            if (caps is not null)
                CapabilityScopeMerger.MergeFileSystemScope(caps, options);
            return options;
        });
        services.AddSingleton<FileSystemPlugin>();
        services.AddFileSystemCommands(); // generated
        return services;
    }
}
