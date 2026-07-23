import { readFileSync } from "node:fs";
import { join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { repositoryRoot } from "./schema-validation.mjs";

function readPackageFile(...segments) {
  return readFileSync(join(repositoryRoot, "openttd", ...segments), "utf8");
}

export function validateOpenTtdPackages() {
  const errors = [];
  const gameInfo = readPackageFile("game", "ArenaGS", "info.nut");
  const gameMain = readPackageFile("game", "ArenaGS", "main.nut");
  const aiInfo = readPackageFile("ai", "ModelProxyAI", "info.nut");
  const aiMain = readPackageFile("ai", "ModelProxyAI", "main.nut");

  if (!/class ArenaGSInfo extends GSInfo/.test(gameInfo) || !/RegisterGS\(ArenaGSInfo\(\)\);/.test(gameInfo)) {
    errors.push("ArenaGS must expose GSInfo metadata and register it.");
  }

  if (!/class ArenaGS extends GSController/.test(gameMain) || !/function Start\(\)/.test(gameMain)) {
    errors.push("ArenaGS must expose a GSController entry point.");
  }

  if (!/class ModelProxyAIInfo extends AIInfo/.test(aiInfo) || !/RegisterAI\(ModelProxyAIInfo\(\)\);/.test(aiInfo)) {
    errors.push("ModelProxyAI must expose AIInfo metadata and register it.");
  }

  if (!/class ModelProxyAI extends AIController/.test(aiMain) || !/function Start\(\)/.test(aiMain)) {
    errors.push("ModelProxyAI must expose an AIController entry point.");
  }

  if (/AICompany|AIVehicle|AIRoad|AIRail|AIOrder|AIGroup/.test(aiMain)) {
    errors.push("ModelProxyAI must remain inert at the Phase 00 boundary.");
  }

  return errors;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const errors = validateOpenTtdPackages();
  if (errors.length > 0) {
    for (const error of errors) {
      console.error(`OpenTTD package validation failure: ${error}`);
    }

    process.exitCode = 1;
  } else {
    console.log("OpenTTD foundation package metadata is valid");
  }
}
