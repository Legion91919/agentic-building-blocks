# Quality

## Definition of done
- Behaviour covered by focused tests written first (rules as unit tests, HTTP behaviour through a real Kestrel host).
- Builds with zero warnings (warnings as errors, `8.0-recommended` analyzers, XML docs on every public member).
- Block README and `docs/DESIGN.md` updated in the same change; `.agentic/changelog.md` has a line.
- CI green: build, whitespace format, tests with coverage gate (70% line, ratchet up), vulnerable packages, gitleaks, privacy guard.

## Current test counts (offline runner, 2026-11-20)
| Block | Tests |
|---|---|
| Core | 52 |
| Testing | 7 |
| Web | 21 |
| Idempotency | 15 |
| Outbox | 19 |
| Audit | 12 |
| Auth | 39 |
| Notifications | 24 |
| Subscriptions | 50 |
| **Total** | **239, all passing** |

## Service levels for consuming APIs (suggested defaults)
| SLI | SLO |
|---|---|
| API success rate (non-4xx) | 99.9% over 30 days |
| p95 latency, login | < 400 ms (PBKDF2 600k iterations is roughly 150-300 ms of CPU) |
| p95 latency, other endpoints | < 200 ms |
| Outbox delivery lag (enqueue to first attempt) | p95 < 5 s |
| Dead-lettered messages | 0 unacknowledged for more than 1 business day |
| Webhook processing success | 99.9%, duplicates never re-applied |

## What would page someone
Outbox dead letters rising (`agentic.blocks.outbox.dead_lettered`), `/health/ready` failing, login 5xx rate, or the
audit integrity check (`GET /audit/verify`) failing.
