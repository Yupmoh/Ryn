using System.Text.Json.Serialization;

namespace Ryn.Plugins.Notification;

internal readonly record struct NotificationEventPayload(string Id);

[JsonSerializable(typeof(NotificationEventPayload))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class NotificationJsonContext : JsonSerializerContext;
