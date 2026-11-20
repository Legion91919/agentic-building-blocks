# AGENTS.md

Agentic Building Blocks: Teams rebuild login, subscriptions, notifications and idempotency on every project; these blocks give them a tested, documented starting point they own.

## Read first
Everything about this project lives in the hidden `.agentic/` folder.
Start with `.agentic/README.md`; it says which file answers which question.
Load only the files your task needs.

## Hard rules
- Work in thin vertical slices: one ticket from `.agentic/backlog.md` at a time.
- No change without a test that fails first. Run the full test suite before you finish.
- A decision that is hard to reverse gets an ADR in `.agentic/decisions/`.
- Never commit secrets, real client data or personal data. Use `.env.example`.
- Do not grow this file. New knowledge goes in the right `.agentic/` file; history goes in `.agentic/changelog.md`.
- When you learn something that would help on the next project, add it to `.agentic/lessons.md`.

## What is live
Nine packages in `src/` (version 0.1.0), tests per block in `tests/`, `samples/Blocks.Sample.Api`. Offline build/test commands are in `.agentic/runbook.md`. Blocks may depend only on the BCL and shared frameworks (ADR-0001).
