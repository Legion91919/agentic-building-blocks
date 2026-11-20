# Product brief

## Problem
Teams rebuild login, subscriptions, notifications and idempotency on every project. Each rebuild costs weeks, and the
hard parts (lockout timing, duplicate webhooks, retries that charge twice, notifications that block requests) are
easy to get subtly wrong.

## Users
Engineers starting a showcase or client project on .NET 8 who want a tested, documented starting point they own and
can read end to end. Secondary: reviewers and clients reading the portfolio.

## Outcomes
- A new API gets login, ProblemDetails, health, rate limits, idempotent commands, an outbox, audit, notifications and
  subscriptions in under an hour, by adding packages and a few lines in `Program.cs`.
- Zero third-party runtime dependencies; every block builds offline.
- Every block has focused tests (239 today) and a README; design reasoning is in `docs/DESIGN.md`.

## Constraints
- .NET 8 LTS, BCL and shared frameworks only (ADR-0001). NuGet.org is unreachable in the build environment.
- Public portfolio: invented data only, no real company or product names, no secrets.
- Personal data (emails, phones) is never logged unmasked.

## Out of scope
- Persistent adapters (EF Core, distributed caches) and real payment, email or SMS providers: consumers write these against the ports.
- A UI. Blocks are APIs; the sample is headless.
- Multi-region concerns.

## Traits (for the architecture advisor)
MoneyMovement, RegulatoryAudit, PersonalData, ExternalPartnerIntegrations, SeveralBusinessAreas, MultiTenant, SmallTeam.
