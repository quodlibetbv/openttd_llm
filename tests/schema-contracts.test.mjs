import test from "node:test";
import assert from "node:assert/strict";
import { validateSchemaCatalog } from "../scripts/schema-validation.mjs";

test("every versioned contract schema accepts its valid fixture and rejects invalid fixtures", () => {
  const { errors, results } = validateSchemaCatalog();

  assert.deepEqual(errors, []);
  assert.equal(results.length, 11);
  for (const result of results) {
    assert.equal(result.validFiles.length, 1, `${result.schemaName} needs one valid fixture`);
    assert.ok(result.invalidFiles.length >= 2, `${result.schemaName} needs two invalid fixtures`);
  }
});
