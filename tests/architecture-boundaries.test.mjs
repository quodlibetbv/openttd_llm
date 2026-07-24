import test from "node:test";
import assert from "node:assert/strict";
import { checkArchitecture } from "../scripts/check-architecture.mjs";
import { validateOpenTtdPackages } from "../scripts/validate-openttd-packages.mjs";

test("provider adapters cannot reference OpenTTD execution internals", () => {
  assert.deepEqual(checkArchitecture(), []);
});

test("the OpenTTD Phase 02 packages have persisted inert readiness entry points", () => {
  assert.deepEqual(validateOpenTtdPackages(), []);
});
