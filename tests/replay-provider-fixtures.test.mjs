import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync, readdirSync } from "node:fs";
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

test("the synthetic DeepSeek completion fixture is sanitized and contains a schema-valid decision", () => {
  const fixturePath = join(repositoryRoot, "tests", "fixtures", "providers", "deepseek-chat-completion.v1.sanitized.json");
  const modelDecisionSchemaPath = join(repositoryRoot, "schemas", "actions", "model-decision.v1.json");
  const fixtureText = readFileSync(fixturePath, "utf8");
  const fixture = JSON.parse(fixtureText);
  const modelDecisionSchema = JSON.parse(readFileSync(modelDecisionSchemaPath, "utf8"));
  const decision = JSON.parse(fixture.choices[0].message.content);

  assert.deepEqual(scanTextForSecrets(fixtureText), []);
  assert.equal(fixture.id, "chatcmpl-sanitized-0001");
  assert.deepEqual(validateInstance(decision, modelDecisionSchema), []);
});

test("the Phase 06 replay road fixture is sanitized and selects one typed route action", () => {
  const fixturePath = join(repositoryRoot, "replays", "phase-06-road-smoke.v1.json");
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
  assert.equal(fixture.steps[0].decision.actions.length, 1);
  assert.equal(fixture.steps[0].decision.actions[0].tool, "build_transport_route");
});

test("synthetic observation fixtures cover the declared strategic states", () => {
  const fixtureDirectory = join(repositoryRoot, "tests", "fixtures", "observations");
  const observationSchemaPath = join(repositoryRoot, "schemas", "observations", "observation.v1.json");
  const observationSchema = JSON.parse(readFileSync(observationSchemaPath, "utf8"));
  const names = readdirSync(fixtureDirectory).sort();

  assert.deepEqual(names, [
    "bankruptcy-risk.v1.json",
    "congestion.v1.json",
    "debt-stress.v1.json",
    "early-game.v1.json",
    "profitable-company.v1.json"
  ]);
  for (const name of names) {
    const fixtureText = readFileSync(join(fixtureDirectory, name), "utf8");
    assert.deepEqual(scanTextForSecrets(fixtureText), [], name);
    assert.deepEqual(validateInstance(JSON.parse(fixtureText), observationSchema), [], name);
  }
});

test("the secret scanner rejects a credential-shaped value without recording one", () => {
  const syntheticToken = ["sk-", "a".repeat(24)].join("");

  assert.deepEqual(scanTextForSecrets(syntheticToken), ["OpenAI-style token"]);
});
