import { mkdtemp, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";
import { AgentRuntime, ModuleImplementation } from "../../sir-thaddeus-core/src/assistant/AgentRuntime.js";
import type { ToolContext } from "../src/coreContracts.js";
import { normalizeModuleManifest } from "../../sir-thaddeus-core/src/modules/ModuleManifest.js";
import { createHealthPackMcpClient } from "../src/mcp/HealthPackMcpClient.js";
import { HealthPackRuntime } from "../src/HealthPackRuntime.js";
import { healthPackManifest } from "../src/manifest.js";
import { createHealthPackModule } from "../src/module.js";
import { DailyHealthSnapshot, HealthBaseline, PatternEpisode, StrategyBrief } from "../src/models.js";
import { GoogleHealthProvider } from "../src/providers/GoogleHealthProvider.js";
import { InMemoryProviderConfigStore } from "../src/providers/ProviderConfigStore.js";
import { FileTokenStore, InMemoryTokenStore } from "../src/providers/TokenStore.js";
import { BaselineService } from "../src/services/BaselineService.js";
import { SignalDetector } from "../src/services/SignalDetector.js";
import { SimilarPastDaysService } from "../src/services/SimilarPastDaysService.js";
import { FileHealthStore } from "../src/storage/FileHealthStore.js";
import { InMemoryHealthStore } from "../src/storage/InMemoryHealthStore.js";

type TestCase = {
  name: string;
  run: () => Promise<void> | void;
};

const tests: TestCase[] = [
  {
    name: "manifest declares the Health Pack contract Core needs",
    run: () => {
      const manifest = normalizeModuleManifest(healthPackManifest);

      assertEqual(manifest.id, "com.thaddeus.health");
      assertIncludes(manifest.tools, "health.get_daily_snapshot");
      assertIncludes(manifest.tools, "health.refresh_daily_snapshot");
      assertIncludes(manifest.tools, "health.get_baselines");
      assertIncludes(manifest.tools, "health.get_morning_strategy_brief");
      assertIncludes(manifest.tools, "health.get_similar_past_days");
      assertIncludes(manifest.tools, "health.log_manual_checkin");
      assertIncludes(manifest.tools, "health.provider_status");
      assertIncludes(manifest.tools, "health.provider_config_schema");
      assertIncludes(manifest.tools, "health.secret_store_status");
      assertIncludes(manifest.tools, "health.set_provider_config");
      assertIncludes(manifest.tools, "health.clear_provider_config");
      assertIncludes(manifest.tools, "health.start_provider_auth");
      assertIncludes(manifest.tools, "health.complete_provider_auth");
      assertIncludes(manifest.tools, "health.disconnect_provider");
      assertIncludes(manifest.tools, "health.provider_audit_events");
      assertIncludes(manifest.tools, "health.sync_range");
      assertIncludes(manifest.tools, "health.backfill");
      assertIncludes(manifest.jobs, "health.morning_strategy_job");
      assertIncludes(manifest.permissions.externalAccounts[0].scopes, "sleep.read");
      assertIncludes(manifest.permissions.externalAccounts[0].scopes, "heart_rate.read");
      assertIncludes(manifest.permissions.externalAccounts[0].scopes, "hrv.read");
      assertIncludes(manifest.permissions.externalAccounts[0].scopes, "activity.read");
      assertIncludes(manifest.memoryNamespaces, "daily_health_snapshots");
      assertIncludes(manifest.memoryNamespaces, "daily_strategy_briefs");
      assertIncludes(manifest.memoryNamespaces, "manual_health_checkins");
    }
  },
  {
    name: "Core runs install hooks while keeping tools permission-gated",
    run: async () => {
      let installed = false;
      const module: ModuleImplementation = {
        manifest: {
          id: "com.thaddeus.test",
          name: "Test Module",
          version: "0.1.0",
          permissions: { memory: { write: ["test"] } },
          tools: ["test.echo"],
          hooks: ["on_module_installed"],
          memoryNamespaces: ["test"]
        },
        tools: {
          "test.echo": async () => "ok"
        },
        hooks: {
          on_module_installed: async () => {
            installed = true;
          }
        }
      };

      const runtime = new AgentRuntime();
      await runtime.installModule(module);

      assertEqual(installed, true);
      await assertRejects(() => runtime.invokeTool({ name: "test.echo", args: {} }), "Permissions pending");

      await runtime.approveModule("com.thaddeus.test");
      assertEqual(await runtime.invokeTool({ name: "test.echo", args: {} }), "ok");
    }
  },
  {
    name: "Mock provider backfills canonical daily snapshots",
    run: async () => {
      const runtime = new HealthPackRuntime({ today: () => "2026-06-02", seedMockHistory: false });

      const result = await runtime.backfill(5, "2026-06-02");
      const snapshots = await runtime.store.listDailySnapshots();

      assertEqual(result.snapshotsStored, 5);
      assert(result.warnings.some((warning) => warning.includes("Mock data")), "mock backfill should report data-quality warnings");
      assertEqual(snapshots.length, 5);
      assertEqual(snapshots[0].date, "2026-05-29");
      assertEqual(snapshots[4].date, "2026-06-02");
      assertEqual(snapshots[4].provider, "mock");
      assertEqual(snapshots[4].dataQuality?.quality, "mock");
      assert(snapshots[4].heart?.restingHeartRate !== undefined, "canonical heart summary should be present");
      assert(snapshots[4].activity?.priorDayLoad !== undefined, "canonical activity load summary should be present");
    }
  },
  {
    name: "baselines use rolling personal history and exclude the strategy date",
    run: async () => {
      const store = new InMemoryHealthStore();
      for (let index = 1; index <= 14; index += 1) {
        await store.saveDailySnapshot(snapshot(`2026-05-${pad(index)}`, {
          sleepMinutes: 420,
          restingHeartRate: 60,
          hrv: 50,
          steps: 7000,
          energy: 6,
          stress: 4
        }));
      }
      await store.saveDailySnapshot(snapshot("2026-05-15", {
        sleepMinutes: 60,
        restingHeartRate: 100,
        hrv: 5,
        steps: 250,
        energy: 1,
        stress: 10
      }));

      const baseline = await new BaselineService(store).calculate("2026-05-15");

      assertEqual(baseline.sleepDuration7DayAverage, 420);
      assertEqual(baseline.sleepDuration14DayAverage, 420);
      assertEqual(baseline.restingHeartRate14DayAverage, 60);
      assertEqual(baseline.hrv14DayAverage, 50);
      assertEqual(baseline.steps7DayAverage, 7000);
      assertEqual(baseline.energy7DayAverage, 6);
      assertEqual(baseline.stress7DayAverage, 4);
      assertEqual(baseline.sampleCounts.sleep14, 14);
    }
  },
  {
    name: "signal detection uses baseline-relative flags and classifies red, blue, and gold days",
    run: () => {
      const detector = new SignalDetector();
      const baseline: HealthBaseline = {
        date: "2026-06-02",
        sleepDuration14DayAverage: 450,
        restingHeartRate14DayAverage: 60,
        hrv14DayAverage: 55,
        steps7DayAverage: 8000,
        stress7DayAverage: 4,
        sampleCounts: {}
      };

      const redSnapshot = snapshot("2026-06-02", {
        sleepMinutes: 260,
        restingHeartRate: 68,
        hrv: 40,
        steps: 2200,
        protein: 50,
        stress: 9,
        soreness: 8,
        workoutIntensity: "hard",
        mood: "flat"
      });
      const redSignals = detector.detect(redSnapshot, baseline);

      assertFlags(redSignals, [
        "sleep_very_low",
        "hrv_low",
        "resting_hr_high",
        "hard_workout",
        "protein_low",
        "stress_high",
        "mood_low",
        "soreness_high",
        "low_activity",
        "drift_risk"
      ]);
      assertEqual(detector.classifyDay(redSignals, redSnapshot, baseline), "red");

      const blueSnapshot = snapshot("2026-06-03", {
        sleepMinutes: 450,
        restingHeartRate: 60,
        hrv: 55,
        steps: 8000,
        stress: 8,
        mood: "low"
      });
      const blueSignals = detector.detect(blueSnapshot, baseline);
      assertEqual(detector.classifyDay(blueSignals, blueSnapshot, baseline), "blue");

      const goldSnapshot = snapshot("2026-06-04", {
        sleepMinutes: 510,
        restingHeartRate: 58,
        hrv: 64,
        steps: 9000,
        stress: 3,
        mood: "steady"
      });
      const goldSignals = detector.detect(goldSnapshot, baseline);
      assertEqual(detector.classifyDay(goldSignals, goldSnapshot, baseline), "gold");
    }
  },
  {
    name: "Morning brief flags elevated RHR, poor sleep, and high prior-day load",
    run: async () => {
      const store = new InMemoryHealthStore();
      for (let index = 1; index <= 14; index += 1) {
        await store.saveDailySnapshot(snapshot(`2026-05-${pad(index)}`, {
          sleepMinutes: 450,
          restingHeartRate: 60,
          hrv: 55,
          steps: 8000
        }));
      }

      const runtime = new HealthPackRuntime({ store, seedMockHistory: false });
      await store.saveDailySnapshot(snapshot("2026-05-15", {
        sleepMinutes: 290,
        restingHeartRate: 69,
        hrv: 54,
        steps: 9500,
        workoutIntensity: "hard"
      }));

      const brief = await runtime.getMorningStrategyBrief("2026-05-15");
      const flags = brief.keySignals.map((signal) => signal.flag);

      assertIncludes(flags, "sleep_very_low");
      assertIncludes(flags, "resting_hr_high");
      assertIncludes(flags, "prior_day_load_high");
      assertEqual(brief.readinessLevel, "recovery");
      assert(brief.baselineComparisons.some((item) => item.metric === "restingHeartRate" && item.direction === "above"), "RHR comparison should be above baseline");
      assert(brief.similarDayPatterns.length > 0, "similar day placeholder/pattern summary should be present");
      assert(brief.caveats.some((item) => item.includes("planning") || item.includes("healthcare guidance")), "brief should include practical-planning caveat");
    }
  },
  {
    name: "similar past days rank overlapping pattern episodes and handle missing history",
    run: async () => {
      const store = new InMemoryHealthStore();
      await store.savePatternEpisode(episode("2026-05-01", ["sleep_low", "protein_low", "drift_risk"]));
      await store.savePatternEpisode(episode("2026-05-02", ["stress_high"]));
      await store.savePatternEpisode(episode("2026-05-03", ["sleep_low", "stress_high", "drift_risk"]));

      const service = new SimilarPastDaysService(store);
      const matches = await service.find("2026-06-02", ["sleep_low", "drift_risk"], 3);
      const noMatches = await service.find("2026-06-02", ["hrv_low"], 3);

      assertEqual(matches.length, 2);
      assertEqual(matches[0].date, "2026-05-03");
      assertEqual(matches[0].overlappingFlags.length, 2);
      assertEqual(noMatches.length, 0);
    }
  },
  {
    name: "Health Pack runtime stores snapshots, check-ins, briefs, jobs, and similar seeded days",
    run: async () => {
      const date = "2026-06-02";
      const core = new AgentRuntime();
      await core.installModule(createHealthPackModule({
        today: () => date,
        store: new InMemoryHealthStore()
      }));
      await core.approveModule("com.thaddeus.health");

      const snapshotResult = await core.invokeTool<unknown, DailyHealthSnapshot>({
        name: "health.get_daily_snapshot",
        args: { date }
      });
      const baseline = await core.invokeTool<unknown, HealthBaseline>({
        name: "health.get_baselines",
        args: { date }
      });
      const brief = await core.invokeTool<unknown, StrategyBrief>({
        name: "health.get_morning_strategy_brief",
        args: { date }
      });
      const checkin = await core.invokeTool({
        name: "health.log_manual_checkin",
        args: {
          date,
          subjective: { energy: 4, stress: 8, soreness: 6, mood: "flat", focus: 4 },
          nutrition: { proteinEstimate: 48, hydrationEstimate: 50 },
          notes: "Meaningful unit test check-in."
        }
      });
      const similar = await core.invokeTool<unknown, unknown[]>({
        name: "health.get_similar_past_days",
        args: { date }
      });
      const jobBrief = await core.runJob<StrategyBrief>("health.morning_strategy_job");

      assertEqual(snapshotResult.date, date);
      assertEqual(baseline.date, date);
      assertEqual(brief.date, date);
      assert(brief.keySignals.length > 0, "brief should include key signals");
      assert(brief.baselineComparisons.length > 0, "brief should include baseline comparisons");
      assert(brief.recommendations.length > 0, "brief should include recommendations");
      assert(brief.nonNegotiable.length > 0, "brief should include a non-negotiable");
      assert(!brief.likelyContributors.some((item) => /condition|illness|disease/i.test(item.reason)), "brief should not make healthcare condition claims");
      assertEqual((checkin as { date: string }).date, date);
      assert(similar.length > 0, "seeded mock history should produce similar days");
      assertEqual(jobBrief.date, date);
    }
  },
  {
    name: "manual check-ins merge subjective and nutrition data into the daily snapshot",
    run: async () => {
      const runtime = new HealthPackRuntime({ today: () => "2026-06-02", seedMockHistory: false });
      await runtime.refreshDailySnapshot("2026-06-02");
      await runtime.logManualCheckin({
        date: "2026-06-02",
        subjective: { energy: 3, stress: 9, mood: "flat" },
        nutrition: { proteinEstimate: 45, hydrationEstimate: 40 },
        notes: "Dragging today."
      });

      const updated = await runtime.getDailySnapshot("2026-06-02");
      const checkins = await runtime.store.listManualCheckins("2026-06-02");

      assertEqual(updated.subjective?.energy, 3);
      assertEqual(updated.subjective?.stress, 9);
      assertEqual(updated.nutrition?.proteinEstimate, 45);
      assertEqual(checkins.length, 1);
    }
  },
  {
    name: "file-backed local store persists Health Pack collections across instances",
    run: async () => {
      const dir = await mkdtemp(join(tmpdir(), "thaddeus-health-pack-"));
      try {
        const filePath = join(dir, "health-store.json");
        const store = new FileHealthStore(filePath);
        const date = "2026-06-02";
        await store.saveDailySnapshot(snapshot(date, {
          sleepMinutes: 420,
          restingHeartRate: 60,
          hrv: 52,
          steps: 7200
        }));
        await store.saveBaseline({
          date,
          sleepDuration7DayAverage: 420,
          sampleCounts: { sleep7: 7 }
        });
        await store.saveManualCheckin({
          id: "checkin_test",
          date,
          subjective: { energy: 5, stress: 6 },
          notes: "Sensitive local check-in note.",
          createdAt: "2026-06-02T08:00:00.000Z"
        });
        const raw = await readFile(filePath, "utf8");

        const reloaded = new FileHealthStore(filePath);
        assertEqual((await reloaded.getDailySnapshot(date))?.sleep?.durationMinutes, 420);
        assertEqual((await reloaded.getBaseline(date))?.sampleCounts.sleep7, 7);
        assertEqual((await reloaded.listManualCheckins(date))[0].notes, "Sensitive local check-in note.");
        assert(raw.includes("\"encrypted\": true"), "health store should be encrypted at rest by default");
        assert(!raw.includes("Sensitive local check-in note"), "health store must not expose check-in notes as plaintext");
        assert(!raw.includes("daily_health_snapshots"), "health store must not expose collection names as plaintext");
      } finally {
        await rm(dir, { recursive: true, force: true });
      }
    }
  },
  {
    name: "Provider status is sanitized and never returns secrets",
    run: async () => {
      const provider = new GoogleHealthProvider({
        clientId: "client-id-secret",
        clientSecret: "client-secret-value",
        redirectUri: "http://localhost:8787/oauth/callback",
        accessToken: "access-token-secret"
      });
      const runtime = new HealthPackRuntime({ provider, seedMockHistory: false });

      const status = await runtime.getProviderStatus();
      const serialized = JSON.stringify(status);

      assertEqual(status.providerName, "google-health");
      assertEqual(status.lifecycle, "connected");
      assertEqual(status.authenticated, true);
      assert(!serialized.includes("client-id-secret"), "provider status must not include client id value");
      assert(!serialized.includes("client-secret-value"), "provider status must not include client secret value");
      assert(!serialized.includes("access-token-secret"), "provider status must not include access token value");
    }
  },
  {
    name: "Provider lifecycle transitions and config tools are sanitized",
    run: async () => {
      const configStore = new InMemoryProviderConfigStore();
      const tokenStore = new InMemoryTokenStore();
      const runtime = new HealthPackRuntime({
        providerConfigStore: configStore,
        tokenStore,
        seedMockHistory: false
      });

      const initial = await runtime.getProviderStatus();
      assertEqual(initial.lifecycle, "connected");
      assertEqual(initial.providerName, "mock");

      const configured = await runtime.setProviderConfig({
        selectedProvider: "google-health",
        googleHealth: {
          clientId: "google-client-id",
          clientSecret: "google-client-secret",
          redirectUri: "http://localhost:8787/oauth/callback",
          accessToken: "google-access-token"
        }
      }) as { status: { lifecycle: string; credentials: { clientSecret?: boolean; accessToken?: boolean } } };
      const serialized = JSON.stringify(configured);

      assertEqual(configured.status.lifecycle, "connected");
      assertEqual(configured.status.credentials.clientSecret, true);
      assertEqual(configured.status.credentials.accessToken, true);
      assert(!serialized.includes("google-client-secret"), "config response must not expose client secret");
      assert(!serialized.includes("google-access-token"), "config response must not expose access token");

      await runtime.disconnectProvider();
      const disconnected = await runtime.getProviderStatus();
      assertEqual(disconnected.lifecycle, "auth_required");
      assertEqual(disconnected.credentials.accessToken, false);
    }
  },
  {
    name: "Missing Google credentials returns clear sanitized setup status",
    run: async () => {
      const runtime = new HealthPackRuntime({
        providerConfigStore: new InMemoryProviderConfigStore({ selectedProvider: "google-health" }),
        tokenStore: new InMemoryTokenStore(),
        seedMockHistory: false
      });

      const status = await runtime.getProviderStatus();
      const auth = await runtime.startProviderAuth();
      const serialized = JSON.stringify({ status, auth });

      assertEqual(status.lifecycle, "not_configured");
      assertIncludes(status.missingConfig, "GOOGLE_HEALTH_CLIENT_ID");
      assert(!status.missingConfig.includes("GOOGLE_HEALTH_CLIENT_SECRET"), "desktop PKCE auth should not require a client secret");
      assertEqual((auth as { lifecycle: string }).lifecycle, "not_configured");
      assertIncludes(status.scopes, "https://www.googleapis.com/auth/fitness.sleep.read");
      assertIncludes(status.scopes, "https://www.googleapis.com/auth/fitness.heart_rate.read");
      assertIncludes(status.scopes, "https://www.googleapis.com/auth/fitness.activity.read");
      assert(!status.scopes.includes("hrv.read"), "Google OAuth scopes should not include placeholder HRV scope names");
      assert(!serialized.includes("GOOGLE_HEALTH_CLIENT_SECRET"), "missing credential status should not require client secret");
      assert(!serialized.includes("client-secret-value"), "missing credential status should not expose secret values");
    }
  },
  {
    name: "Google Health desktop auth uses PKCE without requiring a client secret",
    run: async () => {
      let tokenRequest: URLSearchParams | undefined;
      const tokenStore = new InMemoryTokenStore();
      const provider = new GoogleHealthProvider({
        clientId: "desktop-client-id",
        redirectUri: "http://127.0.0.1:8787/oauth/callback",
        scopes: ["https://www.googleapis.com/auth/fitness.sleep.read"],
        tokenStore,
        fetch: async (_url, init) => {
          tokenRequest = init?.body as URLSearchParams;
          return new Response(JSON.stringify({
            access_token: "pkce-access-token",
            refresh_token: "pkce-refresh-token",
            expires_in: 3600,
            scope: "https://www.googleapis.com/auth/fitness.sleep.read"
          }), {
            status: 200,
            headers: { "Content-Type": "application/json" }
          });
        }
      });

      const status = await provider.getStatus();
      const start = await provider.startAuth("expected-state");
      const authUrl = new URL(start.authUrl ?? "");
      const verifierBeforeComplete = (await tokenStore.get("google-health")).authCodeVerifier;
      const complete = await provider.completeAuth("oauth-code");
      const stored = await tokenStore.get("google-health");

      assertEqual(status.lifecycle, "auth_required");
      assertEqual(status.configured, true);
      assertEqual(status.credentials.clientSecret, false);
      assertEqual(start.publicClient, true);
      assertEqual(start.codeChallengeMethod, "S256");
      assertEqual(authUrl.searchParams.get("code_challenge_method"), "S256");
      const codeChallenge = authUrl.searchParams.get("code_challenge");
      assert(Boolean(codeChallenge && codeChallenge.length > 20), "auth URL should include a PKCE challenge");
      assertEqual(authUrl.searchParams.has("client_secret"), false);
      assert(verifierBeforeComplete && verifierBeforeComplete.length >= 43, "PKCE verifier should be stored locally before completion");
      assertEqual(complete.connected, true);
      assertEqual(tokenRequest?.get("client_secret"), null);
      assertEqual(tokenRequest?.get("code_verifier"), verifierBeforeComplete);
      assertEqual(stored.accessToken, "pkce-access-token");
      assertEqual(stored.refreshToken, "pkce-refresh-token");
      assertEqual(stored.authCodeVerifier, undefined);
    }
  },
  {
    name: "OAuth completion requires the saved auth state when one exists",
    run: async () => {
      const runtime = new HealthPackRuntime({
        providerConfigStore: new InMemoryProviderConfigStore({
          selectedProvider: "google-health",
          googleHealth: {
            clientId: "client-id",
            redirectUri: "http://localhost:8787/oauth/callback",
            scopes: ["sleep.read"]
          },
          authState: {
            provider: "google-health",
            state: "expected-state",
            startedAt: "2026-06-03T08:00:00.000Z"
          }
        }),
        tokenStore: new InMemoryTokenStore(),
        seedMockHistory: false
      });

      await runtime.tokenStore.set("google-health", { clientSecret: "client-secret" });
      const result = await runtime.completeProviderAuth({ code: "auth-code" }) as { lifecycle: string; connected: boolean; message: string };
      const audit = await runtime.getAuditEvents();
      const serialized = JSON.stringify({ result, audit });

      assertEqual(result.lifecycle, "error");
      assertEqual(result.connected, false);
      assert(result.message.includes("state mismatch"), "OAuth completion should explain state mismatch");
      assert(audit.some((event) => event.result === "denied" && event.action === "health.provider_connected"), "state mismatch should be audited as denied");
      assert(!serialized.includes("client-secret"), "OAuth state failure must not expose stored client secret");
    }
  },
  {
    name: "Token storage is isolated from provider config responses and audit events",
    run: async () => {
      const tokenStore = new InMemoryTokenStore();
      const runtime = new HealthPackRuntime({
        providerConfigStore: new InMemoryProviderConfigStore(),
        tokenStore,
        seedMockHistory: false
      });

      await runtime.setProviderConfig({
        selectedProvider: "google-health",
        googleHealth: {
          clientId: "client-id",
          clientSecret: "client-secret-that-should-not-leak",
          redirectUri: "http://localhost:8787/oauth/callback",
          accessToken: "access-token-that-should-not-leak"
        }
      });

      const stored = await tokenStore.get("google-health");
      const config = await runtime.getProviderConfig();
      const audit = await runtime.getAuditEvents();
      const auditTool = await runtime.tools()["health.provider_audit_events"]({ limit: 10 }, {
        moduleId: "com.thaddeus.health",
        eventBus: noopEventBus()
      });
      const publicText = JSON.stringify({ config, audit });
      const toolText = JSON.stringify(auditTool);

      assertEqual(stored.clientSecret, "client-secret-that-should-not-leak");
      assertEqual(stored.accessToken, "access-token-that-should-not-leak");
      assert(!publicText.includes("client-secret-that-should-not-leak"), "provider config/audit must not expose client secret");
      assert(!publicText.includes("access-token-that-should-not-leak"), "provider config/audit must not expose access token");
      assert(!toolText.includes("client-secret-that-should-not-leak"), "provider audit tool must not expose client secret");
      assert(!toolText.includes("access-token-that-should-not-leak"), "provider audit tool must not expose access token");
    }
  },
  {
    name: "File token store keeps secrets out of plaintext local storage",
    run: async () => {
      const dir = await mkdtemp(join(tmpdir(), "thaddeus-health-tokens-"));
      try {
        const tokenPath = join(dir, "provider-tokens.local.json");
        const store = new FileTokenStore(tokenPath);
        await store.set("google-health", {
          clientSecret: "client-secret-file-token",
          accessToken: "access-token-file-token",
          refreshToken: "refresh-token-file-token"
        });

        const raw = await readFile(tokenPath, "utf8");
        const roundTrip = await store.get("google-health");
        const protection = store.protectionStatus();

        assertEqual(roundTrip.clientSecret, "client-secret-file-token");
        assertEqual(roundTrip.accessToken, "access-token-file-token");
        assertEqual(roundTrip.refreshToken, "refresh-token-file-token");
        assert(!raw.includes("client-secret-file-token"), "stored token file must not contain plaintext client secret");
        assert(!raw.includes("access-token-file-token"), "stored token file must not contain plaintext access token");
        assert(!raw.includes("refresh-token-file-token"), "stored token file must not contain plaintext refresh token");
        if (process.platform === "win32") {
          assert(raw.includes("dpapi:"), "Windows token file should use DPAPI-protected values");
          assertEqual(protection.backend, "windows-dpapi");
        }
        assertEqual(protection.localOnly, true);
      } finally {
        await rm(dir, { recursive: true, force: true });
      }
    }
  },
  {
    name: "Sync range stores canonical snapshots and morning brief uses stored data",
    run: async () => {
      const runtime = new HealthPackRuntime({ today: () => "2026-06-03", seedMockHistory: false });

      const sync = await runtime.syncRange("2026-06-01", "2026-06-03");
      const brief = await runtime.getMorningStrategyBrief("2026-06-03");
      const status = await runtime.getProviderStatus();
      const audit = await runtime.getAuditEvents();

      assertEqual(sync.snapshotsStored, 3);
      assertEqual(status.sync?.snapshotCount, 3);
      assertEqual(brief.date, "2026-06-03");
      assert(brief.caveats.length > 0, "brief should include caveats");
      assert(audit.some((event) => event.action === "health.sync_completed"), "sync completion should be audited");
      assert(audit.some((event) => event.action === "health.brief_generated"), "brief generation should be audited");
    }
  },
  {
    name: "MCP adapter exposes and calls Health Pack tools",
    run: async () => {
      const client = createHealthPackMcpClient({ today: () => "2026-06-02" });
      const tools = await client.listTools();
      const brief = await client.callTool("health.get_morning_strategy_brief", { date: "2026-06-02" }) as StrategyBrief;

      assertIncludes(tools.map((tool) => tool.name), "health.get_morning_strategy_brief");
      assertIncludes(tools.map((tool) => tool.name), "health.set_provider_config");
      assertIncludes(tools.map((tool) => tool.name), "health.secret_store_status");
      assertIncludes(tools.map((tool) => tool.name), "health.provider_audit_events");
      assertEqual(brief.date, "2026-06-02");
      assert(brief.recommendations.length > 0, "MCP brief should include recommendations");
      await assertRejects(() => client.callTool("health.unknown", {}), "not available");
    }
  },
  {
    name: "stdio MCP server registers and executes Health Pack tools",
    run: async () => {
      const dir = await mkdtemp(join(tmpdir(), "thaddeus-health-mcp-"));
      const client = new Client({ name: "health-pack-test-client", version: "0.1.0" });
      const transport = new StdioClientTransport({
        command: process.execPath,
        args: ["dist/mcp/server.js"],
        cwd: process.cwd(),
        stderr: "pipe",
        env: {
          ...process.env,
          HEALTH_DATA_PROVIDER: "mock",
          HEALTH_STORE_PATH: join(dir, "health-store.json"),
          HEALTH_PROVIDER_CONFIG_PATH: join(dir, "provider-config.json"),
          HEALTH_TOKEN_STORE_PATH: join(dir, "provider-tokens.local.json"),
          HEALTH_AUDIT_PATH: join(dir, "health-audit.jsonl"),
          GOOGLE_HEALTH_CLIENT_ID: "",
          GOOGLE_HEALTH_CLIENT_SECRET: "",
          GOOGLE_HEALTH_ACCESS_TOKEN: "",
          GOOGLE_HEALTH_REFRESH_TOKEN: ""
        } as Record<string, string>
      });

      try {
        await client.connect(transport);
        const tools = await client.listTools();
        const result = await client.callTool({
          name: "health.get_morning_strategy_brief",
          arguments: { date: "2026-06-02" }
        });
        const statusResult = await client.callTool({
          name: "health.provider_status",
          arguments: {}
        });
        const content = (result as {
          content: Array<{ type: string; text?: string }>;
        }).content;
        const statusContent = (statusResult as {
          content: Array<{ type: string; text?: string }>;
        }).content;

        assertIncludes(tools.tools.map((tool) => tool.name), "health.get_morning_strategy_brief");
        assertIncludes(tools.tools.map((tool) => tool.name), "health.provider_status");
        assertIncludes(tools.tools.map((tool) => tool.name), "health.secret_store_status");
        assertIncludes(tools.tools.map((tool) => tool.name), "health.provider_audit_events");
        assert(
          content.some((item) => item.type === "text" && item.text?.includes("\"date\": \"2026-06-02\"")),
          "stdio MCP brief should include the requested date"
        );
        assert(
          !statusContent.some((item) => item.text?.includes("GOOGLE_HEALTH_CLIENT_SECRET=")),
          "stdio MCP provider status should not expose secret values"
        );
      } finally {
        await client.close();
        await rm(dir, { recursive: true, force: true });
      }
    }
  },
  {
    name: "stdio MCP provider status does not return provider secrets",
    run: async () => {
      const dir = await mkdtemp(join(tmpdir(), "thaddeus-health-mcp-secrets-"));
      const client = new Client({ name: "health-pack-secret-test-client", version: "0.1.0" });
      const transport = new StdioClientTransport({
        command: process.execPath,
        args: ["dist/mcp/server.js"],
        cwd: process.cwd(),
        stderr: "pipe",
        env: {
          ...process.env,
          HEALTH_STORE_PATH: join(dir, "health-store.json"),
          HEALTH_PROVIDER_CONFIG_PATH: join(dir, "provider-config.json"),
          HEALTH_TOKEN_STORE_PATH: join(dir, "provider-tokens.local.json"),
          HEALTH_AUDIT_PATH: join(dir, "health-audit.jsonl"),
          HEALTH_DATA_PROVIDER: "google-health",
          GOOGLE_HEALTH_CLIENT_ID: "secret-client-id",
          GOOGLE_HEALTH_CLIENT_SECRET: "secret-client-secret",
          GOOGLE_HEALTH_ACCESS_TOKEN: "secret-token"
        } as Record<string, string>
      });

      try {
        await client.connect(transport);
        const result = await client.callTool({
          name: "health.provider_status",
          arguments: {}
        });
        const content = (result as {
          content: Array<{ type: string; text?: string }>;
        }).content.map((item) => item.text ?? "").join("\n");

        assert(content.includes("\"providerName\": \"google-health\""), "provider status should identify google-health mode");
        assert(!content.includes("secret-client-id"), "MCP provider status must not include client id value");
        assert(!content.includes("secret-client-secret"), "MCP provider status must not include client secret value");
        assert(!content.includes("secret-token"), "MCP provider status must not include access token value");
      } finally {
        await client.close();
        await rm(dir, { recursive: true, force: true });
      }
    }
  },
  {
    name: "Google Health provider is a clear scaffold until OAuth is configured",
    run: async () => {
      const provider = new GoogleHealthProvider();
      await assertRejects(() => provider.getDailySnapshot("2026-06-02"), "access token is not configured");
    }
  }
];

async function runTests(): Promise<void> {
  const failures: string[] = [];
  for (const test of tests) {
    try {
      await test.run();
      console.log(`PASS ${test.name}`);
    } catch (error) {
      const message = error instanceof Error ? error.stack ?? error.message : String(error);
      failures.push(`FAIL ${test.name}\n${message}`);
      console.error(failures[failures.length - 1]);
    }
  }

  if (failures.length > 0) {
    throw new Error(`${failures.length} Health Pack test${failures.length === 1 ? "" : "s"} failed.`);
  }
}

function snapshot(date: string, values: {
  sleepMinutes: number;
  restingHeartRate: number;
  hrv: number;
  steps: number;
  energy?: number;
  stress?: number;
  soreness?: number;
  protein?: number;
  workoutIntensity?: DailyHealthSnapshot["activity"] extends infer Activity
    ? Activity extends { workoutIntensity?: infer Intensity }
      ? Intensity
      : never
    : never;
  mood?: string;
}): DailyHealthSnapshot {
  return {
    date,
    sleep: { durationMinutes: values.sleepMinutes },
    recovery: { restingHeartRate: values.restingHeartRate, hrv: values.hrv },
    heart: { restingHeartRate: values.restingHeartRate, hrv: values.hrv },
    activity: {
      steps: values.steps,
      workoutIntensity: values.workoutIntensity ?? "none",
      workoutMinutes: values.workoutIntensity === "hard" ? 60 : 0,
      priorDayLoad: values.workoutIntensity === "hard" ? "high" : "normal"
    },
    nutrition: { proteinEstimate: values.protein ?? 110 },
    subjective: {
      energy: values.energy ?? 6,
      stress: values.stress ?? 4,
      soreness: values.soreness ?? 3,
      mood: values.mood ?? "steady"
    }
  };
}

function episode(date: string, flags: PatternEpisode["flags"]): PatternEpisode {
  return {
    date,
    flags,
    dayType: "yellow",
    summary: `Seeded episode for ${date}.`,
    after: { energy: 6, focus: 6, mood: "steady" },
    recommendationsHelped: true,
    repeatedImprovement: "A walk and an early work block helped."
  };
}

function pad(value: number): string {
  return value.toString().padStart(2, "0");
}

function assert(condition: unknown, message: string): asserts condition {
  if (!condition) {
    throw new Error(message);
  }
}

function assertEqual<T>(actual: T, expected: T): void {
  if (actual !== expected) {
    throw new Error(`Expected ${format(expected)}, got ${format(actual)}.`);
  }
}

function assertIncludes<T>(values: readonly T[], expected: T): void {
  if (!values.includes(expected)) {
    throw new Error(`Expected ${format(values)} to include ${format(expected)}.`);
  }
}

function assertFlags(signals: { flag: string }[], flags: string[]): void {
  const actual = signals.map((signal) => signal.flag);
  for (const flag of flags) {
    assertIncludes(actual, flag);
  }
}

async function assertRejects(action: () => Promise<unknown>, expectedMessage: string): Promise<void> {
  try {
    await action();
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error);
    if (!message.includes(expectedMessage)) {
      throw new Error(`Expected rejection containing ${format(expectedMessage)}, got ${format(message)}.`);
    }
    return;
  }

  throw new Error(`Expected rejection containing ${format(expectedMessage)}, but the promise resolved.`);
}

function noopEventBus(): ToolContext["eventBus"] {
  return {
    subscribe: () => () => undefined,
    subscribeAll: () => () => undefined,
    publish: async (type, payload, options = {}) => ({
      type,
      payload,
      moduleId: options.moduleId,
      occurredAt: options.occurredAt ?? new Date()
    }),
    clear: () => undefined
  };
}

function format(value: unknown): string {
  return JSON.stringify(value);
}

void runTests();
