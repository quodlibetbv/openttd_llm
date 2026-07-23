import { readFileSync, readdirSync } from "node:fs";
import { join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { repositoryRoot } from "./schema-validation.mjs";

function projectFile(projectName) {
  return join(repositoryRoot, "src", projectName, `${projectName}.csproj`);
}

function sourceFiles(directory) {
  return readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const path = join(directory, entry.name);
    if (entry.isDirectory()) {
      return entry.name === "bin" || entry.name === "obj" ? [] : sourceFiles(path);
    }

    return entry.name.endsWith(".cs") ? [path] : [];
  });
}

export function checkArchitecture() {
  const errors = [];
  const providerProject = readFileSync(projectFile("Arena.Providers"), "utf8");
  const providerSources = sourceFiles(join(repositoryRoot, "src", "Arena.Providers"))
    .map((sourcePath) => readFileSync(sourcePath, "utf8"))
    .join("\n");

  if (!providerProject.includes("../Arena.Contracts/Arena.Contracts.csproj")) {
    errors.push("Arena.Providers must reference the shared contracts project.");
  }

  const providerReferences = [...providerProject.matchAll(/<ProjectReference\s+Include="([^"]+)"/g)]
    .map((match) => match[1].toLowerCase());
  if (providerReferences.some((reference) => !reference.endsWith("arena.contracts/arena.contracts.csproj"))) {
    errors.push("Arena.Providers may reference only Arena.Contracts at the Phase 00 boundary.");
  }

  for (const forbiddenReference of ["arena.adminprotocol", "arena.orchestrator", "arena.camera", "arena.obs", "openttd"]) {
    if (providerReferences.some((reference) => reference.includes(forbiddenReference))) {
      errors.push(`Arena.Providers must not reference ${forbiddenReference}.`);
    }
  }

  if (/OpenTTD|AdminPort|GSController|AIController/.test(providerSources)) {
    errors.push("Provider implementation must not depend on OpenTTD execution internals.");
  }

  const scoringProject = readFileSync(projectFile("Arena.Scoring"), "utf8");
  if (scoringProject.includes("Arena.Providers")) {
    errors.push("Arena.Scoring must not reference provider adapters.");
  }

  return errors;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const errors = checkArchitecture();
  if (errors.length > 0) {
    for (const error of errors) {
      console.error(`architecture validation failure: ${error}`);
    }

    process.exitCode = 1;
  } else {
    console.log("architecture boundaries are closed at Phase 00");
  }
}
