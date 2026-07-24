import { readFileSync, readdirSync, statSync } from "node:fs";
import { dirname, join, resolve, sep } from "node:path";
import { fileURLToPath } from "node:url";

const scriptDirectory = dirname(fileURLToPath(import.meta.url));
export const repositoryRoot = resolve(scriptDirectory, "..");

function isObject(value) {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

function walk(directory) {
  const entries = readdirSync(directory, { withFileTypes: true });
  return entries.flatMap((entry) => {
    const absolutePath = join(directory, entry.name);
    if (entry.isDirectory()) {
      return walk(absolutePath);
    }

    return [absolutePath];
  });
}

function readJson(filePath) {
  try {
    return JSON.parse(readFileSync(filePath, "utf8"));
  } catch (error) {
    throw new Error(`Invalid JSON in ${filePath}: ${error.message}`);
  }
}

function resolveReference(rootSchema, reference) {
  if (!reference.startsWith("#/")) {
    throw new Error(`Only local schema references are supported in Phase 02: ${reference}`);
  }

  return reference
    .slice(2)
    .split("/")
    .map((segment) => segment.replaceAll("~1", "/").replaceAll("~0", "~"))
    .reduce((current, segment) => current?.[segment], rootSchema);
}

function jsonEquals(left, right) {
  return JSON.stringify(left) === JSON.stringify(right);
}

function matchesType(value, expectedType) {
  switch (expectedType) {
    case "object":
      return isObject(value);
    case "array":
      return Array.isArray(value);
    case "string":
      return typeof value === "string";
    case "number":
      return typeof value === "number" && Number.isFinite(value);
    case "integer":
      return typeof value === "number" && Number.isInteger(value);
    case "boolean":
      return typeof value === "boolean";
    case "null":
      return value === null;
    default:
      throw new Error(`Unsupported schema type: ${expectedType}`);
  }
}

function validateNode(value, schema, rootSchema, instancePath, errors) {
  if (!isObject(schema)) {
    errors.push(`${instancePath}: schema node must be an object`);
    return;
  }

  if (typeof schema.$ref === "string") {
    const target = resolveReference(rootSchema, schema.$ref);
    if (!target) {
      errors.push(`${instancePath}: unresolved schema reference ${schema.$ref}`);
      return;
    }

    validateNode(value, target, rootSchema, instancePath, errors);
    return;
  }

  if (isObject(schema.not)) {
    const nestedErrors = [];
    validateNode(value, schema.not, rootSchema, instancePath, nestedErrors);
    if (nestedErrors.length === 0) {
      errors.push(`${instancePath}: must not match the forbidden schema`);
    }
  }

  if (Object.hasOwn(schema, "const") && !jsonEquals(value, schema.const)) {
    errors.push(`${instancePath}: must equal the schema const`);
  }

  if (Array.isArray(schema.enum) && !schema.enum.some((option) => jsonEquals(value, option))) {
    errors.push(`${instancePath}: must equal one of the schema enum values`);
  }

  if (typeof schema.type === "string" && !matchesType(value, schema.type)) {
    errors.push(`${instancePath}: must be a ${schema.type}`);
    return;
  }

  if (typeof value === "string") {
    if (typeof schema.minLength === "number" && value.length < schema.minLength) {
      errors.push(`${instancePath}: must contain at least ${schema.minLength} characters`);
    }

    if (typeof schema.maxLength === "number" && value.length > schema.maxLength) {
      errors.push(`${instancePath}: must contain no more than ${schema.maxLength} characters`);
    }

    if (typeof schema.pattern === "string" && !(new RegExp(schema.pattern).test(value))) {
      errors.push(`${instancePath}: does not match the required pattern`);
    }
  }

  if (typeof value === "number") {
    if (typeof schema.minimum === "number" && value < schema.minimum) {
      errors.push(`${instancePath}: must be at least ${schema.minimum}`);
    }

    if (typeof schema.maximum === "number" && value > schema.maximum) {
      errors.push(`${instancePath}: must be at most ${schema.maximum}`);
    }
  }

  if (Array.isArray(value)) {
    if (typeof schema.minItems === "number" && value.length < schema.minItems) {
      errors.push(`${instancePath}: must contain at least ${schema.minItems} items`);
    }

    if (typeof schema.maxItems === "number" && value.length > schema.maxItems) {
      errors.push(`${instancePath}: must contain no more than ${schema.maxItems} items`);
    }

    if (schema.uniqueItems === true) {
      const serializedItems = value.map((item) => JSON.stringify(item));
      if (new Set(serializedItems).size !== serializedItems.length) {
        errors.push(`${instancePath}: must contain unique items`);
      }
    }

    if (isObject(schema.items)) {
      value.forEach((item, index) => validateNode(item, schema.items, rootSchema, `${instancePath}[${index}]`, errors));
    }
  }

  if (isObject(value)) {
    const propertyNames = Object.keys(value);
    if (typeof schema.minProperties === "number" && propertyNames.length < schema.minProperties) {
      errors.push(`${instancePath}: must contain at least ${schema.minProperties} properties`);
    }

    if (typeof schema.maxProperties === "number" && propertyNames.length > schema.maxProperties) {
      errors.push(`${instancePath}: must contain no more than ${schema.maxProperties} properties`);
    }

    const properties = isObject(schema.properties) ? schema.properties : {};
    if (isObject(schema.propertyNames)) {
      for (const propertyName of propertyNames) {
        validateNode(propertyName, schema.propertyNames, rootSchema, `${instancePath}.{propertyName}`, errors);
      }
    }

    if (Array.isArray(schema.required)) {
      for (const requiredProperty of schema.required) {
        if (!Object.hasOwn(value, requiredProperty)) {
          errors.push(`${instancePath}: is missing required property '${requiredProperty}'`);
        }
      }
    }

    for (const [propertyName, propertyValue] of Object.entries(value)) {
      const propertySchema = properties[propertyName];
      if (propertySchema) {
        validateNode(propertyValue, propertySchema, rootSchema, `${instancePath}.${propertyName}`, errors);
      } else if (schema.additionalProperties === false) {
        errors.push(`${instancePath}: contains unknown property '${propertyName}'`);
      } else if (isObject(schema.additionalProperties)) {
        validateNode(propertyValue, schema.additionalProperties, rootSchema, `${instancePath}.${propertyName}`, errors);
      }
    }
  }
}

export function validateInstance(instance, schema) {
  const errors = [];
  validateNode(instance, schema, schema, "$", errors);
  return errors;
}

function validateSchemaNode(schema, schemaPath, errors) {
  if (!isObject(schema)) {
    errors.push(`${schemaPath}: schema nodes must be objects`);
    return;
  }

  if (typeof schema.$ref === "string" && !schema.$ref.startsWith("#/")) {
    errors.push(`${schemaPath}: Phase 02 contracts may only use local references`);
  }

  if (typeof schema.type === "string" && schema.type === "object" &&
      isObject(schema.properties) && typeof schema.additionalProperties !== "boolean") {
    errors.push(`${schemaPath}: object schemas with properties must explicitly set additionalProperties`);
  }

  if (Array.isArray(schema.required) && !isObject(schema.properties)) {
    errors.push(`${schemaPath}: required properties need an object properties map`);
  }

  if (isObject(schema.properties)) {
    for (const [propertyName, propertySchema] of Object.entries(schema.properties)) {
      validateSchemaNode(propertySchema, `${schemaPath}.properties.${propertyName}`, errors);
    }
  }

  if (isObject(schema.$defs)) {
    for (const [definitionName, definitionSchema] of Object.entries(schema.$defs)) {
      validateSchemaNode(definitionSchema, `${schemaPath}.$defs.${definitionName}`, errors);
    }
  }

  if (isObject(schema.items)) {
    validateSchemaNode(schema.items, `${schemaPath}.items`, errors);
  }

  if (isObject(schema.additionalProperties)) {
    validateSchemaNode(schema.additionalProperties, `${schemaPath}.additionalProperties`, errors);
  }

  if (isObject(schema.propertyNames)) {
    validateSchemaNode(schema.propertyNames, `${schemaPath}.propertyNames`, errors);
  }

  if (isObject(schema.not)) {
    validateSchemaNode(schema.not, `${schemaPath}.not`, errors);
  }
}

function getSchemaFiles() {
  const schemasDirectory = join(repositoryRoot, "schemas");
  return walk(schemasDirectory)
    .filter((filePath) => filePath.endsWith(".v1.json"))
    .filter((filePath) => !filePath.includes(`${sep}examples${sep}`))
    .sort();
}

export function validateSchemaCatalog() {
  const errors = [];
  const results = [];

  for (const schemaPath of getSchemaFiles()) {
    const schema = readJson(schemaPath);
    const schemaName = schemaPath.slice(repositoryRoot.length + 1);
    if (schema.$schema !== "https://json-schema.org/draft/2020-12/schema") {
      errors.push(`${schemaName}: must declare the Draft 2020-12 meta-schema`);
    }

    if (typeof schema.$id !== "string" || schema.$id.length === 0) {
      errors.push(`${schemaName}: must declare a stable $id`);
    }

    if (schema.type !== "object" || schema.additionalProperties !== false) {
      errors.push(`${schemaName}: root must be a closed object schema`);
    }

    if (!Array.isArray(schema.required) || schema.required.length === 0) {
      errors.push(`${schemaName}: root must require its contract fields`);
    }

    validateSchemaNode(schema, schemaName, errors);

    const baseName = schemaPath.split(sep).at(-1).replace(".json", "");
    const examplesDirectory = join(dirname(schemaPath), "examples");
    const exampleFiles = statSync(examplesDirectory).isDirectory()
      ? readdirSync(examplesDirectory).filter((fileName) => fileName.startsWith(baseName))
      : [];
    const validFiles = exampleFiles.filter((fileName) => fileName.endsWith(".valid.json"));
    const invalidFiles = exampleFiles.filter((fileName) => fileName.includes(".invalid-"));

    if (validFiles.length !== 1) {
      errors.push(`${schemaName}: requires exactly one representative valid example`);
    }

    if (invalidFiles.length < 2) {
      errors.push(`${schemaName}: requires at least two representative invalid examples`);
    }

    for (const validFile of validFiles) {
      const validationErrors = validateInstance(readJson(join(examplesDirectory, validFile)), schema);
      if (validationErrors.length > 0) {
        errors.push(`${schemaName}: valid fixture ${validFile} failed: ${validationErrors.join("; ")}`);
      }
    }

    for (const invalidFile of invalidFiles) {
      const validationErrors = validateInstance(readJson(join(examplesDirectory, invalidFile)), schema);
      if (validationErrors.length === 0) {
        errors.push(`${schemaName}: invalid fixture ${invalidFile} unexpectedly passed`);
      }
    }

    results.push({ schemaName, validFiles, invalidFiles });
  }

  return { errors, results };
}
