# Runbook

## Build (offline workspace)
```bash
for p in src/*/*.csproj samples/*/*.csproj; do dotnet build "$p" --configfile /home/claude/tools/offline-nuget.config; done
```
With internet access: `dotnet build AgenticBuildingBlocks.sln -c Release`.

## Test
- Offline: `/home/claude/tools/run-xunit-offline.sh tests/Agentic.Blocks.<Block>.Tests src/Agentic.Blocks.<Block>/Agentic.Blocks.<Block>.csproj src/Agentic.Blocks.Testing/Agentic.Blocks.Testing.csproj`
- CI / online: `dotnet test AgenticBuildingBlocks.sln -c Release`

## Run the sample
```bash
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:5199 dotnet run --project samples/Blocks.Sample.Api
./samples/smoke.sh        # register, login, me, idempotent POST x2, 402 -> subscribe -> 200, inbox, logout
```
In Development a demo user `demo@example.com` (roles user, admin) is seeded; its random password is printed once to
the console. Nothing is persisted: restarting clears all data.

## Pack and publish
```bash
for b in src/*/; do dotnet pack "$b" -c Release -o artifacts/packages -p:Version=0.1.0 --configfile /home/claude/tools/offline-nuget.config; done
cp artifacts/packages/*.nupkg /tmp/offline-nuget/pkgs/     # local offline feed for other workspace projects
```
CI packs on every push and publishes to GitHub Packages when a `v*` tag is pushed (`git tag v0.1.0 && git push --tags`).
Consumers add `https://nuget.pkg.github.com/<owner>/index.json` as a source with a token that has `read:packages`.

## Configuration a consuming app must decide
| Concern | Default | Production action |
|---|---|---|
| Data Protection keys (bearer tokens) | per-machine key ring | Persist and share keys (`AddDataProtection().PersistKeysTo...`) when running more than one instance |
| Stores | in-memory | Implement the ports on your database (see each block README for the atomicity needed) |
| Email / SMS | log masked recipient only | Register real `IEmailSender` / `ISmsSender` |
| Payment provider | fake, random per-process webhook secret | Implement `IPaymentProvider`; keep the webhook secret in a secret store |
| Rate limits | auth 10/min/IP, API 120/min, webhooks 300/min | Tune in `AddBlocksWeb(o => ...)`; add `UseForwardedHeaders` behind a proxy |

## Common incidents
- **Dead letters growing**: read `LastError` via `IOutboxStore.GetDeadLettersAsync`, fix the cause, `RequeueAsync(id)`.
- **Users locked out in bulk**: likely credential stuffing; lockout is per account for 15 minutes. Check the auth rate-limit rejections (429) by IP.
- **Webhooks rejected with `subscriptions.webhook_signature`**: clock skew over 5 minutes or a rotated secret.
- **Audit verify fails**: someone changed storage directly. The response names the first broken sequence; restore from backup and investigate access.
