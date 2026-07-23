import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { repositoryRoot, validateInstance } from "../scripts/schema-validation.mjs";
import { scanTextForSecrets } from "../scripts/scan-secrets.mjs";

test("the replay fixture is sanitized and contains a schema-valid decision", () => {
  const fixturePath = join(repositoryRoot, "tests", "fixtures", "providers", "replay-decision.v1.json");
  const modelDecisionSchemaPath = join(repositoryRoot, "schemas", "actions", "model-decision.v1.json");
  const fixtureText = readFileSync(fixturePath, "utf8");
  const fixture = JSON.parse(fixtureText);
  const modelDecisionSchema = JSON.parse(readFileSync(modelDecisionSchemaPath, "utf8"));

  assert.deepEqual(scanTextForSecrets(fixtureText), []);
  assert.equal(fixture.fixture_version, "1.0");
  assert.equal(fixture.provider, "replay");
  assert.equal(fixture.steps.length, 1);
  assert.match(fixture.steps[0].expected_observation_sha256, /^[0-9a-f]{64}$/);
  assert.deepEqual(validateInstance(fixture.steps[0].decision, modelDecisionSchema), []);
});

test("the secret scanner rejects a credential-shaped value without recording one", () => {
  const syntheticToken = ["sk-", "a".repeat(24)].join("");

  assert.deepEqual(scanTextForSecrets(syntheticToken), ["OpenAI-style token"]);
});
