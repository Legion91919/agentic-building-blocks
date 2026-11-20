# Next step

- **Doing:** nothing in progress; 0.1.0 is packed and verified offline.
- **Done so far:** nine blocks with tests (239 passing), sample API smoke-tested on port 5199, packages in
  `artifacts/packages` and `/tmp/offline-nuget/pkgs`, docs, ADR-0001, CI.
- **Next action:** push to GitHub, let CI run the real xUnit suite and coverage gate once, then tag `v0.1.0`.
- **Then:** optional `Agentic.Blocks.*.EntityFrameworkCore` adapters (outbox, idempotency, audit, users) once two
  projects need them; a real SignalR client test once the client package can be restored.
- **Blocked by:** nothing.
