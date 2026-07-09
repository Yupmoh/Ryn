# Notifications

`Ryn.Plugins.Notification` delivers native OS notifications and reports when the user clicks them.

```csharp
services.AddRynNotification();
```

```json
{ "capabilities": { "notification": true } }
```

## JS API

```js
// Fire-and-forget
await window.__ryn.invoke('notification.send', { title: 'Build done', body: 'All green' });
await window.__ryn.invoke('notification.sendWithSound', { title: 'Ping', body: '…', sound: 'default' });
await window.__ryn.invoke('notification.sendWithIcon',  { title: 'Saved', body: '…', iconPath: '/path/icon.png' });

// With an id you get back on click:
await window.__ryn.invoke('notification.sendWithId',
  { id: 'order-42', title: 'Shipped', body: 'Tap to view', sound: null, iconPath: null });

// Permission
const supported = await window.__ryn.invoke('notification.isSupported');
const granted   = await window.__ryn.invoke('notification.isPermissionGranted'); // query, no prompt
const result    = await window.__ryn.invoke('notification.requestPermission');   // may prompt → "granted"/"denied"
```

### Activation events

```js
window.__ryn.on('notification.activated', e => { /* { id } — the user clicked it; focus the right thing */ });
window.__ryn.on('notification.dismissed', e => { /* { id } — the OS reported it dismissed (where supported) */ });
```

The `id` is whatever you passed to `sendWithId` (the other `send*` variants use an auto-generated
`auto-N` id). Route the click to the right in-app target off `notification.activated`.

## C# API

`NotificationService` (singleton, resolvable from DI):

```csharp
var notifications = services.GetRequiredService<NotificationService>();
notifications.Activated += id => Focus(id);
notifications.Send("order-42", "Shipped", "Tap to view");
```

## Platform behavior

| | Delivery | Activation (click → event) |
|---|---|---|
| macOS | ✅ always | ✅ when the app is **bundled** (a `.app` with a `CFBundleIdentifier`) via `UNUserNotificationCenter`; unbundled dev runs deliver via `osascript` with no activation |
| Linux | ✅ (libnotify, else `notify-send`) | ✅ when libnotify is present (a clickable default action) |
| Windows | ✅ (WinRT toast) | ⚠️ requires a packaged app / a Start-menu shortcut carrying the app's AUMID and a registered COM activator — Ryn delivers the toast, but click-to-activate needs that packaging state |

**Activation is a published-app feature.** In `dotnet run` / dev mode, notifications are delivered
but clicks are not reported on any platform, because every OS ties click-back to app identity (bundle
id / AUMID). Published Ryn apps that carry that identity get activation. Design the click handler as an
enhancement, not a requirement, and always ship a working non-click flow.
