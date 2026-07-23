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

const textExtensions = new Set([
  ".cs",
  ".csproj",
  ".editorconfig",
  ".gitattributes",
  ".gitignore",
  ".json",
  ".md",
  ".mjs",
  ".nut",
  ".props",
  ".ps1",
  ".sln",
  ".ts",
  ".yaml",
  ".yml",
]);

function extension(fileName) {
  const index = fileName.lastIndexOf(".");
  return index >= 0 ? fileName.slice(index) : "";
}

function walk(directory) {
  return readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const path = join(directory, entry.name);
    if (entry.isDirectory()) {
      return ignoredDirectories.has(entry.name) ? [] : walk(path);
    }

    return textExtensions.has(extension(entry.name)) ? [path] : [];
  });
}

export function checkFormatting() {
  const errors = [];
  for (const filePath of walk(repositoryRoot)) {
    const contents = readFileSync(filePath, "utf8");
    const displayPath = relative(repositoryRoot, filePath);
    if (!contents.endsWith("\n")) {
      errors.push(`${displayPath}: must end with a newline`);
    }

    contents.split(/\r?\n/).forEach((line, index) => {
      if (/[ \t]+$/.test(line)) {
        errors.push(`${displayPath}:${index + 1}: contains trailing whitespace`);
      }
    });

    if (filePath.endsWith(".json")) {
      try {
        JSON.parse(contents);
      } catch (error) {
        errors.push(`${displayPath}: invalid JSON (${error.message})`);
      }
    }
  }

  return errors;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const errors = checkFormatting();
  if (errors.length > 0) {
    for (const error of errors) {
      console.error(`format validation failure: ${error}`);
    }

    process.exitCode = 1;
  } else {
    console.log("text formatting and JSON syntax are valid");
  }
}
