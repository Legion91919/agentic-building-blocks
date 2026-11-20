# Agentic.Blocks.Notifications

Tell users things on the channels they chose, without making the caller wait or fail.

```bash
dotnet add package Agentic.Blocks.Notifications --version 0.1.0
```

```csharp
builder.Services.AddBlocksNotifications(o => o
    .AddTemplate("renewal-failed", "Payment failed", "Hi {{name}}, we could not charge your card for {{plan}}.")
    .SetCategoryDefaults("billing", NotificationChannel.InApp, NotificationChannel.Email));
app.UseBlocksOutbox();
app.MapBlocksNotifications("/notifications");

await notifications.SendAsync(userId, category: "billing", templateKey: "renewal-failed",
    new Dictionary<string, string> { ["name"] = "Ada", ["plan"] = "Plus" });
```

**Flow.** `SendAsync` validates the template and stages a `notifications.requested.v1` outbox message in the current
unit of work. After commit, a handler reads the user's preferences and stages one `notifications.delivery.v1` message
per enabled channel. Each channel retries and dead-letters on its own, so a failing email provider never blocks the
caller, the in-app inbox or SMS. Redelivered in-app messages are de-duplicated by notification id.

**Channels and ports.**
- In-app: `IInboxStore` (inbox, unread count, mark read) and `INotificationPusher` (default: SignalR
  `NotificationsHub`, events `notification` and `unreadCount`, sent to `Clients.User(userId)`).
- Email: `IEmailSender`; SMS: `ISmsSender`. Development defaults log only a masked recipient (`a***@example.com`,
  `*******1234`) and the message length, never subject, body or data.
- Contacts: `INotificationRecipientResolver` (default in-memory directory; the sample resolves from the Auth user store).
- Preferences: `INotificationPreferenceStore`; effective channels = user override, else category default, else in-app.

**Endpoints** (signed-in user): `GET inbox?cursor&limit`, `GET inbox/unread-count`, `POST inbox/{id}/read`,
`POST inbox/read-all`, `GET preferences`, `PUT preferences {category, channel, enabled}`, hub at `hub`.
For browser WebSockets add the hub path to `AuthOptions.QueryStringTokenPaths`.
