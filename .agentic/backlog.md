# Backlog

## Now
- [x] Core, Testing, Web, Idempotency, Outbox, Audit, Auth, Notifications, Subscriptions (0.1.0)
- [x] Sample API + smoke script, pack + offline restore check, CI

## Next
- [ ] First CI run with real xUnit and the coverage gate; raise the gate to the measured value
- [ ] Persistent adapters as separate packages (EF Core) for outbox, idempotency, audit and users
- [ ] Outbox: optional ordering key per aggregate; dead-letter admin endpoints
- [ ] Auth: password change and reset flow, email verification

## Later
- [ ] OpenTelemetry wiring example in the sample (needs the OTel packages)
- [ ] Prorated plan changes in Subscriptions
