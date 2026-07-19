using Microsoft.Extensions.DependencyInjection;
using Ryn.Core;
using System.Text.Json;

namespace Ryn.Plugins.Notification;

public sealed class NotificationPlugin : IRynPlugin
{
    private readonly IServiceProvider _services;

    public NotificationPlugin(IServiceProvider services) => _services = services;

    public string Name => "notification";

    public ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        var service = _services.GetRequiredService<NotificationService>();
        service.Activated += id => Emit("notification.activated", id);
        service.Dismissed += id => Emit("notification.dismissed", id);
        return ValueTask.CompletedTask;
    }

    private void Emit(string eventName, string id)
    {
        var webView = _services.GetService<IRynWebView>();
        webView?.EmitEvent(
            eventName,
            JsonSerializer.Serialize(
                new NotificationEventPayload(id),
                NotificationJsonContext.Default.NotificationEventPayload));
    }
}
