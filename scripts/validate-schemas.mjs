import { validateSchemaCatalog } from "./schema-validation.mjs";

const { errors, results } = validateSchemaCatalog();

if (errors.length > 0) {
  for (const error of errors) {
    console.error(`schema validation failure: ${error}`);
  }

  process.exitCode = 1;
} else {
  console.log(`validated ${results.length} closed versioned schemas and their fixtures`);
}
