# Agentic.Blocks.Auth

Email and password login on the built-in .NET 8 BearerToken handler. No packages beyond the shared framework.

```bash
dotnet add package Agentic.Blocks.Auth --version 0.1.0
```

```csharp
builder.Services.AddBlocksWeb().AddBlocksAuth(o =>
{
    o.QueryStringTokenPaths.Add("/notifications/hub");          // SignalR WebSockets
    o.DemoUsers.Add(new DemoUser { Email = "demo@example.com" }); // Development only; password generated and logged once
});
app.UseBlocksWeb();
app.MapBlocksAuth("/auth");
app.MapGet("/admin", () => "ok").RequireAuthorization(p => p.RequireRole("admin"));
```

| Endpoint | Behaviour |
|---|---|
| `POST /auth/register` `{email,password}` | 201 with the user; 400 field errors; 409 `auth.email_taken`. Rate-limited. |
| `POST /auth/login` | 200 `{tokenType, accessToken, expiresIn, refreshToken}`; 401 `auth.invalid_credentials` or `auth.locked_out` (+`retryAfterSeconds`). Rate-limited. |
| `POST /auth/refresh` `{refreshToken}` | New token pair; 401 if expired, tampered or revoked. |
| `POST /auth/logout` | Rotates the security stamp: every access and refresh token of the user stops working at once. |
| `GET /auth/me` | `{id, email, roles, tenantId}`. |

**Passwords.** PBKDF2-HMAC-SHA256 via `Rfc2898DeriveBytes.Pbkdf2`, 600,000 iterations, 16-byte random salt per user,
32-byte key, stored as `v1$pbkdf2-sha256$600000$<salt>$<key>`. Comparison uses `CryptographicOperations.FixedTimeEquals`.
When you raise the parameters, old hashes still verify and are re-hashed on the next successful login.
Unknown emails run a dummy hash so timing does not reveal which accounts exist.

**Lockout.** 5 consecutive failures lock the account for 15 minutes (configurable). During lockout even the right
password is refused and further attempts do not extend it. A successful login resets the counter.

**Claims.** `NameIdentifier`, `Name`, `Email`, `Role` (one per role), `tenant_id` (optional) and a security stamp.

## Swapping in an external identity provider (OIDC)
App code depends on claims, not on this block: endpoints use `RequireAuthorization`, and other blocks call
`ClaimsPrincipal.GetUserId()` (which reads `NameIdentifier` or the OIDC `sub`) and `GetTenantId()`.
To move to an OIDC provider:
1. Remove `AddBlocksAuth()` and `MapBlocksAuth()`.
2. Add the provider's handler: `AddJwtBearer` for APIs (package `Microsoft.AspNetCore.Authentication.JwtBearer`) or
   `AddOpenIdConnect` + cookies for server-rendered apps, pointing at the provider's authority.
3. If the provider uses different claim names, add an `IClaimsTransformation` that maps them to `role` and `tenant_id`.
4. Point any `IUserStore` consumers (for example a notification recipient resolver) at the provider's user API, or
   keep a local profile table keyed by `sub`.
No endpoint, filter or other block changes. The reverse also holds: implement `IUserStore` over your own database to
keep this block and persist users.

**Why opaque tokens?** See `docs/DESIGN.md`: they are data-protected, need no signing-key management and work
offline, but only this app can read them. Persist the Data Protection key ring when you run more than one instance.
