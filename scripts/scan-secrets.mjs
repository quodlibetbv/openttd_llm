import { readFileSync, readdirSync } from "node:fs";
import { join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { repositoryRoot } from "./schema-validation.mjs";

const ignoredDirectories = new Set([
  ".git",
  ".config",
  ".runtime",
  ".tmp",
  "artifacts",
  "bin",
  "coverage",
  "dist",
  "logs",
  "node_modules",
  "obj",
]);

const patterns = [
  { name: "OpenAI-style token", expression: /\bsk-[A-Za-z0-9]{20,}\b/g },
  { name: "GitHub token", expression: /\bgh[pousr]_[A-Za-z0-9]{20,}\b/g },
  { name: "Google API token", expression: /\bAIza[A-Za-z0-9_-]{20,}\b/g },
  { name: "Bearer credential", expression: /\bBearer\s+[A-Za-z0-9._~-]{20,}\b/g },
  { name: "private key block", expression: /-----BEGIN(?: [A-Z]+)? PRIVATE KEY-----/g },
];

function walk(directory) {
  return readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const path = join(directory, entry.name);
    if (entry.isDirectory()) {
      return ignoredDirectories.has(entry.name) ? [] : walk(path);
    }

    return [path];
  });
}

export function scanTextForSecrets(contents) {
  return patterns
    .flatMap(({ name, expression }) => {
      expression.lastIndex = 0;
      return expression.test(contents) ? [name] : [];
    });
}

export function scanRepositoryForSecrets() {
  const violations = [];
  for (const filePath of walk(repositoryRoot)) {
    const contents = readFileSync(filePath, "utf8");
    const matches = scanTextForSecrets(contents);
    for (const match of matches) {
      violations.push(`${relative(repositoryRoot, filePath)}: ${match}`);
    }
  }

  return violations;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const violations = scanRepositoryForSecrets();
  if (violations.length > 0) {
    for (const violation of violations) {
      console.error(`secret scan failure: ${violation}`);
    }

    process.exitCode = 1;
  } else {
    console.log("secret scan found no credential-shaped values");
  }
}
