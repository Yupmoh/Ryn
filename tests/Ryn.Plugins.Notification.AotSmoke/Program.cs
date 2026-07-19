using Microsoft.Extensions.DependencyInjection;
using Ryn.Plugins.Notification;

var services = new ServiceCollection();
services.AddRynNotification();
using var provider = services.BuildServiceProvider();

var plugin = provider.GetRequiredService<NotificationPlugin>();
await plugin.InitializeAsync().ConfigureAwait(false);
Console.WriteLine(plugin.Name);
