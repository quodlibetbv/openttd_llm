import test from "node:test";
import assert from "node:assert/strict";
import { demoSnapshot, reduceOverlayState } from "../dist/state.js";

test("the reducer preserves the last known snapshot while disconnected", () => {
  const result = reduceOverlayState(demoSnapshot, { type: "disconnected" });

  assert.equal(result?.publicSummary, demoSnapshot.publicSummary);
  assert.equal(result?.isConnected, false);
});

test("a snapshot replaces stale state deterministically", () => {
  const next = {
    ...demoSnapshot,
    gameDate: "1950-01-02",
    isConnected: true,
  };

  assert.deepEqual(reduceOverlayState(null, { type: "snapshot", snapshot: next }), next);
});
