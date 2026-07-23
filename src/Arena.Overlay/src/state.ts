export interface OverlaySnapshot {
  readonly runId: string;
  readonly provider: string;
  readonly model: string;
  readonly gameDate: string;
  readonly publicSummary: string;
  readonly executionStatus: string;
  readonly score: number | null;
  readonly isConnected: boolean;
}

export type OverlayEvent =
  | { readonly type: "snapshot"; readonly snapshot: OverlaySnapshot }
  | { readonly type: "disconnected" }
  | { readonly type: "connected" };

export function reduceOverlayState(
  current: OverlaySnapshot | null,
  event: OverlayEvent,
): OverlaySnapshot | null {
  if (event.type === "snapshot") {
    return event.snapshot;
  }

  if (current === null) {
    return null;
  }

  return {
    ...current,
    isConnected: event.type === "connected",
  };
}

export const demoSnapshot: OverlaySnapshot = {
  runId: "demo-run-0001",
  provider: "replay",
  model: "foundation-fixture",
  gameDate: "1950-01-01",
  publicSummary: "Inspecting the initial scenario before selecting an allowed action.",
  executionStatus: "ready",
  score: null,
  isConnected: true,
};
