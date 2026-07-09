using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Ryn.Core;
using Ryn.Ipc;
using Ryn.Plugins.Notification;
using Xunit;

namespace Ryn.Plugins.Tests;

public sealed class NotificationServiceTests
{
    private sealed class FakeBackend : INotificationBackend
    {
        public readonly List<NotificationRequest> Sent = [];
        public bool Supported = true;
        public bool Granted;
        public event Action<string>? Activated;
        public event Action<string>? Dismissed;

        public bool IsSupported => Supported;
        public bool IsPermissionGranted() => Granted;
        public bool RequestPermission() { Granted = true; return true; }
        public void Send(NotificationRequest request) => Sent.Add(request);
        public void RaiseActivated(string id) => Activated?.Invoke(id);
        public void RaiseDismissed(string id) => Dismissed?.Invoke(id);
        public void Dispose() { }
    }

    [Fact]
    public void Send_ForwardsToBackend_WithId()
    {
        using var backend = new FakeBackend();
        using var service = new NotificationService(backend);

        service.Send("order-42", "Shipped", "Your order is on its way", sound: "ping", iconPath: "/i.png");

        backend.Sent.Should().ContainSingle();
        var req = backend.Sent[0];
        req.Id.Should().Be("order-42");
        req.Title.Should().Be("Shipped");
        req.Body.Should().Be("Your order is on its way");
        req.Sound.Should().Be("ping");
        req.IconPath.Should().Be("/i.png");
    }

    [Fact]
    public void Send_RejectsEmptyId()
    {
        using var backend = new FakeBackend();
        using var service = new NotificationService(backend);
        service.Invoking(s => s.Send("", "t", "b")).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ActivationAndDismissal_SurfaceThroughService()
    {
        using var backend = new FakeBackend();
        using var service = new NotificationService(backend);
        string? activated = null, dismissed = null;
        service.Activated += id => activated = id;
        service.Dismissed += id => dismissed = id;

        backend.RaiseActivated("order-42");
        backend.RaiseDismissed("order-7");

        activated.Should().Be("order-42");
        dismissed.Should().Be("order-7");
    }

    [Fact]
    public void PermissionQuery_IsDistinctFromRequest()
    {
        using var backend = new FakeBackend { Granted = false };
        using var service = new NotificationService(backend);

        service.IsPermissionGranted().Should().BeFalse();   // query does not grant
        service.RequestPermission().Should().Be("granted");
        service.IsPermissionGranted().Should().BeTrue();
    }
}

public sealed class NotificationDependencyInjectionTests
{
    [Fact]
    public void AddRynNotification_RegistersServiceCommandsAndPlugin()
    {
        var services = new ServiceCollection();
        services.AddRynNotification();
        using var sp = services.BuildServiceProvider();

        sp.GetRequiredService<NotificationService>().Should().NotBeNull();
        sp.GetServices<IRynPlugin>().Should().Contain(p => p.Name == "notification");
        sp.GetServices<ICommandRouter>().Should().NotBeEmpty();
    }
}
