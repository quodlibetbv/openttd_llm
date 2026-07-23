# Phase 09 — Cinematic Camera Director

## Objective

Keep the recording focused on meaningful game activity through deterministic event ranking, multiple spectator views, and controlled scene transitions.

## Goals

- Show where the current model action occurs.
- Follow interesting vehicles and route openings.
- Avoid rapid cuts, repetitive shots, dead air, and unrelated UI dialogs.
- Keep camera logic independent from gameplay and scoring.

## Deliverables

1. Three spectator-client profiles with fixed wide, medium, and close zoom levels.
2. Stable client registration and viewport-control protocol.
3. Camera-event schema with subject, tile, event type, importance, duration, and expiration.
4. Shot-selection state machine.
5. OBS scene switching with configured transitions.
6. Vehicle-follow mode implemented through periodic viewport centering.
7. Establishing shots, action shots, milestone shots, and result transitions.
8. Camera-event NDJSON log and offline replay visualizer.

## Initial event priorities

```text
100  Bankruptcy, crash, or terminal failure
90   Major junction or corridor completion
85   First vehicle on a new route
80   First delivery on a new route
75   Town population milestone
70   Record profit or company-value milestone
65   Major expansion or upgrade
60   Congestion or operational problem
50   New vehicle generation introduced
30   Routine construction
```

Scenario files may adjust relevance but may not affect score.

## Shot rules

- Minimum and maximum shot duration.
- Cooldown before revisiting the same location.
- High-priority events may interrupt low-priority shots.
- Construction projects receive a pre-action establishing shot and post-action verification shot.
- Vehicle following ends on timeout, invalid vehicle, or higher-priority event.
- Return to a wide network shot after a sequence of close views.
- Periodically show map-wide progress even during low activity.

## Acceptance criteria

- The active construction location is visible for the majority of action execution time in a representative run.
- No camera cut occurs faster than the configured minimum except for terminal events.
- The same camera-event log produces the same scene-selection sequence.
- Loss of one spectator client degrades to remaining clients without stopping the benchmark.
- Camera requests cannot alter company state, game speed, or score.
- A review sample contains route openings, vehicle follow, milestone shots, and wide progress shots without manual intervention.

## Out of scope

- Post-production montage editing.
- AI-generated narration.
- Pixel-based event detection.

## Exit condition

Phase 09 is complete when an unedited recording consistently shows the location and consequence of major model decisions and remains watchable during long unattended runs.
