import { createConfiguredHealthRuntime } from "./runtimeFactory.js";

type Command =
  | "refresh"
  | "snapshot"
  | "baselines"
  | "brief"
  | "similar"
  | "checkin"
  | "backfill"
  | "sync-range"
  | "provider-status"
  | "provider-schema"
  | "secret-store-status"
  | "set-provider"
  | "clear-provider"
  | "start-auth"
  | "disconnect-provider";

async function main(): Promise<void> {
  const command = (process.argv[2] ?? "brief") as Command;
  const date = readDateArg() ?? readPositionalDateArg() ?? todayIso();
  const runtime = createConfiguredHealthRuntime();

  switch (command) {
    case "provider-status":
      printJson(await runtime.getProviderStatus());
      return;
    case "provider-schema":
      printJson(await runtime.getProviderConfigSchema());
      return;
    case "secret-store-status":
      printJson(await runtime.getSecretStoreStatus());
      return;
    case "set-provider":
      printJson(await runtime.setProviderConfig(parseJsonArg(process.argv[3])));
      return;
    case "clear-provider":
      printJson(await runtime.clearProviderConfig());
      return;
    case "start-auth":
      printJson(await runtime.startProviderAuth());
      return;
    case "disconnect-provider":
      printJson(await runtime.disconnectProvider());
      return;
    case "sync-range":
      printJson(await runtime.syncRange(readRequiredFlag("--start-date"), readRequiredFlag("--end-date")));
      return;
    case "backfill":
      printJson(await runtime.backfill(readDaysArg(), readDateArg() ?? todayIso()));
      return;
    case "refresh":
      printJson(await runtime.refreshDailySnapshot(date));
      return;
    case "snapshot":
      printJson(await runtime.getDailySnapshot(date));
      return;
    case "baselines":
      printJson(await runtime.getBaselines(date));
      return;
    case "brief":
      printJson(await runtime.getMorningStrategyBrief(date));
      return;
    case "similar":
      printJson(await runtime.getSimilarPastDays(date));
      return;
    case "checkin":
      printJson(await runtime.logManualCheckin({ date, ...parseJsonArg(process.argv[4]) }));
      return;
    default:
      console.error(`Unknown command: ${command}. Try provider-status, provider-schema, secret-store-status, set-provider, clear-provider, start-auth, disconnect-provider, sync-range, refresh, snapshot, baselines, brief, similar, checkin, or backfill.`);
      process.exit(1);
  }
}

function readPositionalDateArg(): string | undefined {
  return process.argv[3] && !process.argv[3].startsWith("--") ? process.argv[3] : undefined;
}

function readDaysArg(): number {
  const daysFlag = process.argv.findIndex((arg) => arg === "--days");
  if (daysFlag >= 0) {
    const parsed = Number(process.argv[daysFlag + 1]);
    return Number.isFinite(parsed) ? Math.max(1, Math.floor(parsed)) : 30;
  }

  return 30;
}

function readDateArg(): string | undefined {
  const dateFlag = process.argv.findIndex((arg) => arg === "--date" || arg === "--through-date");
  if (dateFlag >= 0 && process.argv[dateFlag + 1]) {
    return process.argv[dateFlag + 1];
  }

  return undefined;
}

function readRequiredFlag(name: string): string {
  const index = process.argv.findIndex((arg) => arg === name);
  if (index < 0 || !process.argv[index + 1]) {
    throw new Error(`${name} is required.`);
  }
  return process.argv[index + 1];
}

function todayIso(): string {
  return new Date().toISOString().slice(0, 10);
}

function parseJsonArg(raw: string | undefined): Record<string, unknown> {
  if (!raw) {
    return {};
  }

  const parsed = JSON.parse(raw) as unknown;
  if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) {
    throw new Error("Check-in payload must be a JSON object.");
  }

  return parsed as Record<string, unknown>;
}

function printJson(value: unknown): void {
  console.log(JSON.stringify(value, null, 2));
}

main().catch((error) => {
  console.error("Health Pack failed:", error instanceof Error ? error.message : String(error));
  process.exit(1);
});
