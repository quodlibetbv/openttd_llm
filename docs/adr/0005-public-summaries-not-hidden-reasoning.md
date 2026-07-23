# ADR 0005: Public summaries rather than hidden reasoning

- Status: Accepted
- Date: 2026-07-23

## Context

Published recordings need an understandable explanation of model intent, but private chain-of-thought and raw provider payloads are not necessary for reproducibility and must not enter artifacts.

## Decision

The common decision contract includes a concise `public_summary` and bounded observation bullets intended for publication. The platform never requests, stores, logs, displays, or tries to recover hidden reasoning. Raw prompts, provider response bodies, headers, and credentials remain outside public artifacts.

## Consequences

- Schemas cap public text length and reject unknown fields such as private reasoning.
- The overlay renders provider text through safe text APIs and enforces display limits in later phases.
- Diagnostics keep technical error classification without serializing sensitive provider content.

## Migration impact

Adding a new public field requires schema versioning and a publication review. Existing artifacts are not retroactively enriched from provider transcripts.
