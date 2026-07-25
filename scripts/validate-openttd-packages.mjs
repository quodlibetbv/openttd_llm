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

  if (!/function GetShortName\(\)\s*\{\s*return "ARGS";\s*\}/.test(gameInfo) ||
      !/function GetAPIVersion\(\)\s*\{\s*return "14";\s*\}/.test(gameInfo)) {
    errors.push("ArenaGS must declare the supported ARGS short name and GameScript API 14.");
  }

  if (!/class ArenaGS extends GSController/.test(gameMain) ||
      !/function Start\(\)/.test(gameMain) ||
      !/function Save\(\)/.test(gameMain) ||
      !/function Load\(/.test(gameMain) ||
      !/ARENA_PHASE02_GAMESCRIPT_READY/.test(gameMain) ||
      !/GSEventAdminPort\.Convert\(event\)\.GetObject\(\)/.test(gameMain) ||
      !/GSAdmin\.Send\(/.test(gameMain) ||
      !/PROTOCOL_VERSION = "1\.0"/.test(gameMain) ||
      !/function ReplayLedgerResult\(/.test(gameMain) ||
      !/function AcceptChunk\(/.test(gameMain) ||
      !/ARENA-PROTOCOL-CHUNK-TIMEOUT/.test(gameMain) ||
      !/function IsValidScenarioConstraintContext\(/.test(gameMain) ||
      !/if \(action\.rawin\("constraint_context"\) && !this\.ScenarioAllowsTool\(action\)\)/.test(gameMain) ||
      !/action\.tool == "repay_loan" &&\s*GSCompany\.GetBankBalance\(company_id\) - amount < action\.constraint_context\.minimum_cash_reserve/.test(gameMain) ||
      !/quarterly_expenses = this\.NonNegative\(-GSCompany\.GetQuarterlyExpenses\(company_id, GSCompany\.CURRENT_QUARTER\)\)/.test(gameMain)) {
    errors.push("ArenaGS must expose the Phase 03-07 persisted AdminPort and scenario-constraint boundary.");
  }

  if (!/class ModelProxyAIInfo extends AIInfo/.test(aiInfo) || !/RegisterAI\(ModelProxyAIInfo\(\)\);/.test(aiInfo)) {
    errors.push("ModelProxyAI must expose AIInfo metadata and register it.");
  }

  if (!/function GetShortName\(\)\s*\{\s*return "MPAI";\s*\}/.test(aiInfo) ||
      !/function GetAPIVersion\(\)\s*\{\s*return "1\.0";\s*\}/.test(aiInfo)) {
    errors.push("ModelProxyAI must declare the supported MPAI short name and AI API 1.0.");
  }

  if (!/class ModelProxyAI extends AIController/.test(aiMain) ||
      !/function Start\(\)/.test(aiMain) ||
      !/function Save\(\)/.test(aiMain) ||
      !/function Load\(/.test(aiMain) ||
      !/ARENA_PHASE02_MODEL_PROXY_READY/.test(aiMain)) {
    errors.push("ModelProxyAI must expose the Phase 02 persisted readiness entry points.");
  }

  if (/AICompany|AIVehicle|AIRoad|AIRail|AIOrder|AIGroup/.test(aiMain)) {
    errors.push("ModelProxyAI must remain inert at the Phase 02 lifecycle boundary.");
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
    console.log("OpenTTD package metadata is valid for Phases 03-07");
  }
}
