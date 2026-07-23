# Phase 08 — Recording and Decision Sidebar

## Objective

Produce a complete unattended video with synchronized gameplay, human-readable model decisions, execution status, and final results.

## Goals

- Control OBS through its authenticated local WebSocket interface.
- Render a reliable 512-pixel sidebar as a local Browser Source.
- Keep overlay state synchronized with run, decision, action, and score events.
- Finalize recording paths and media metadata as run artifacts.

## Deliverables

1. OBS client for connection, scene inspection, scene switching, recording start/stop, and output verification.
2. Arena scene-collection template and validation.
3. Overlay web application and local run-scoped WebSocket/SSE service.
4. Overlay state reducer with reconnect and latest-snapshot replay.
5. Starting, active, results, and failure views.
6. Recording artifact finalizer and media-duration validation.
7. Deterministic overlay demo mode for visual testing.
8. Optional generation of title, description, chapter, and benchmark metadata drafts.

## Sidebar content

- Provider and model.
- Goal title and active restrictions.
- Game date and run progress.
- Current plan and concise public explanation.
- Selected observations supporting the action.
- Tool execution progress and result.
- Cash, recent profit, score, and goal-specific metrics.
- Provider latency and optional cost.

## Design requirements

- Escape provider-generated content.
- Enforce line, character, and item limits.
- Use large typography suitable for 1440p output.
- Show stale/disconnected state without freezing the game.
- Never show credentials, raw API messages, hidden reasoning, or internal stack traces.
- Preserve the last valid state while reconnecting.

## Acceptance criteria

- One command launches the run, starts recording, and stops recording without OBS interaction.
- The first visible model decision is synchronized with the corresponding game pause and action.
- Reconnecting the overlay reconstructs current state without losing the run.
- OBS or overlay failure is classified and does not corrupt gameplay artifacts.
- A completed recording includes a starting slate, gameplay, and final result scene.
- Media duration approximately matches the recorded run interval and the output file is finalized before run completion is reported.
- Visual tests confirm legibility at the target resolution.

## Out of scope

- Dynamic cinematic camera selection.
- Automated video editing beyond scene switching and result slates.

## Exit condition

Phase 08 is complete when a road-profit run can be uploaded as an understandable unedited video with no manual OBS or overlay operation.
