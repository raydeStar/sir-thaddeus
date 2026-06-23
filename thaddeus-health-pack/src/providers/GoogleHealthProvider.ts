import { createHash, randomBytes } from "node:crypto";
import { DailyHealthSnapshot } from "../models.js";
import { HealthProvider, HealthProviderStatus, ProviderLifecycleStatus } from "./HealthProvider.js";
import { defaultGoogleScopes } from "./ProviderConfigStore.js";
import { ProviderTokenBundle, TokenStore } from "./TokenStore.js";

export interface GoogleHealthProviderOptions {
  clientId?: string;
  clientSecret?: string;
  redirectUri?: string;
  accessToken?: string;
  refreshToken?: string;
  scopes?: string[];
  fetch?: typeof fetch;
  apiBaseUrl?: string;
  authBaseUrl?: string;
  tokenUrl?: string;
  tokenStore?: TokenStore;
}

export interface GoogleAuthStartResult {
  providerName: "google-health";
  lifecycle: ProviderLifecycleStatus;
  authUrl?: string;
  state?: string;
  redirectUri?: string;
  codeChallengeMethod?: "S256";
  publicClient: boolean;
  missingConfig: string[];
  message: string;
}

export interface GoogleAuthCompleteResult {
  providerName: "google-health";
  lifecycle: ProviderLifecycleStatus;
  connected: boolean;
  message: string;
}

export class GoogleHealthProvider implements HealthProvider {
  readonly providerName = "google-health";
  private readonly fetchImpl: typeof fetch;
  private readonly apiBaseUrl: string;
  private readonly authBaseUrl: string;
  private readonly tokenUrl: string;
  private readonly scopes: string[];

  constructor(private readonly options: GoogleHealthProviderOptions = {}) {
    this.fetchImpl = options.fetch ?? fetch;
    this.apiBaseUrl = options.apiBaseUrl ?? "https://health.googleapis.com";
    this.authBaseUrl = options.authBaseUrl ?? "https://accounts.google.com/o/oauth2/v2/auth";
    this.tokenUrl = options.tokenUrl ?? "https://oauth2.googleapis.com/token";
    this.scopes = normalizeScopes(options.scopes);
  }

  async getDailySnapshot(date: string): Promise<DailyHealthSnapshot> {
    const status = await this.getStatus();
    if (!status.authenticated) {
      throw new Error("Google Health access token is not configured. Authorization is required before syncing health data.");
    }

    const warnings = [
      "Google Health API mapping is production-shaped but unavailable until real endpoint access is configured."
    ];

    try {
      await this.fetchJson("/v1/status", { method: "GET" });
    } catch (error) {
      warnings.push(sanitizeError(error));
    }

    return {
      date,
      provider: this.providerName,
      dataQuality: {
        provider: this.providerName,
        quality: "partial",
        generatedAt: new Date().toISOString(),
        missing: ["sleep", "heart", "activity"],
        warnings
      }
    };
  }

  async getStatus(): Promise<HealthProviderStatus> {
    const tokens = await this.readTokens();
    const clientSecret = tokens.clientSecret ?? this.options.clientSecret;
    const accessToken = tokens.accessToken ?? this.options.accessToken;
    const refreshToken = tokens.refreshToken ?? this.options.refreshToken;
    const missingConfig = [
      !this.options.clientId ? "GOOGLE_HEALTH_CLIENT_ID" : "",
      !this.options.redirectUri ? "GOOGLE_HEALTH_REDIRECT_URI" : ""
    ].filter(Boolean);
    const configured = missingConfig.length === 0;
    const authenticated = Boolean(accessToken || refreshToken);
    const lifecycle: ProviderLifecycleStatus = !configured
      ? "not_configured"
      : authenticated
        ? "connected"
        : "auth_required";

    return {
      providerName: this.providerName,
      selectedProvider: this.providerName,
      lifecycle,
      configured,
      authenticated,
      connected: configured && authenticated,
      mode: configured ? "oauth" : "unavailable",
      missingConfig,
      credentials: {
        clientId: Boolean(this.options.clientId),
        clientSecret: Boolean(clientSecret),
        redirectUri: Boolean(this.options.redirectUri),
        accessToken: Boolean(accessToken),
        refreshToken: Boolean(refreshToken)
      },
      scopes: [...this.scopes],
      warnings: authenticated
        ? ["Google Health is authorized. Real API availability depends on configured Google endpoint access."]
        : ["Google Health OAuth is not connected. Desktop auth uses PKCE; a client secret is optional for legacy web-client setups."],
      errors: []
    };
  }

  async startAuth(state = randomState()): Promise<GoogleAuthStartResult> {
    const missingConfig = [
      !this.options.clientId ? "GOOGLE_HEALTH_CLIENT_ID" : "",
      !this.options.redirectUri ? "GOOGLE_HEALTH_REDIRECT_URI" : ""
    ].filter(Boolean);

    if (missingConfig.length > 0) {
      return {
        providerName: this.providerName,
        lifecycle: "not_configured",
        publicClient: true,
        missingConfig,
        message: "Google Health client id and redirect URI are required before auth can start."
      };
    }

    const codeVerifier = createCodeVerifier();
    const codeChallenge = createCodeChallenge(codeVerifier);
    const clientSecret = (await this.readTokens()).clientSecret ?? this.options.clientSecret;
    await this.options.tokenStore?.set(this.providerName, { authCodeVerifier: codeVerifier });

    const url = new URL(this.authBaseUrl);
    url.searchParams.set("client_id", this.options.clientId!);
    url.searchParams.set("redirect_uri", this.options.redirectUri!);
    url.searchParams.set("response_type", "code");
    url.searchParams.set("scope", this.scopes.join(" "));
    url.searchParams.set("access_type", "offline");
    url.searchParams.set("prompt", "consent");
    url.searchParams.set("state", state);
    url.searchParams.set("code_challenge", codeChallenge);
    url.searchParams.set("code_challenge_method", "S256");

    return {
      providerName: this.providerName,
      lifecycle: "auth_in_progress",
      authUrl: url.toString(),
      state,
      redirectUri: this.options.redirectUri,
      codeChallengeMethod: "S256",
      publicClient: !clientSecret,
      missingConfig: [],
      message: "Open the auth URL in the system browser, approve access, then return with the authorization code."
    };
  }

  async completeAuth(code: string): Promise<GoogleAuthCompleteResult> {
    const status = await this.getStatus();
    if (!status.configured) {
      return {
        providerName: this.providerName,
        lifecycle: "not_configured",
        connected: false,
        message: `Google Health credentials are missing: ${status.missingConfig.join(", ")}.`
      };
    }

    const existing = await this.readTokens();
    const tokens = await this.exchangeCode(code);
    if (this.options.tokenStore) {
      await this.options.tokenStore.clear(this.providerName);
      await this.options.tokenStore.set(this.providerName, {
        clientSecret: existing.clientSecret,
        ...tokens
      });
    }
    return {
      providerName: this.providerName,
      lifecycle: "connected",
      connected: true,
      message: "Google Health authorization completed."
    };
  }

  async disconnect(): Promise<void> {
    await this.options.tokenStore?.clear(this.providerName);
  }

  protected async fetchJson(path: string, init: RequestInit = {}, attempt = 0): Promise<unknown> {
    const token = await this.getUsableAccessToken();
    if (!token) {
      throw new Error("Google Health access token is not configured.");
    }

    const response = await this.fetchImpl(`${this.apiBaseUrl}${path}`, {
      ...init,
      headers: {
        ...init.headers,
        Authorization: `Bearer ${token}`,
        "Content-Type": "application/json"
      }
    });

    if (response.status === 401) {
      const refreshed = await this.refreshAccessToken();
      if (refreshed && attempt < 1) {
        return this.fetchJson(path, init, attempt + 1);
      }
      throw new Error("Google Health authorization was rejected or revoked.");
    }

    if (response.status === 403) {
      throw new Error("Google Health returned insufficient scopes or permission denied.");
    }

    if (response.status === 404) {
      throw new Error("Google Health data endpoint is unavailable for this account.");
    }

    if (response.status === 429 || response.status >= 500) {
      throw new Error(`Google Health is temporarily unavailable (HTTP ${response.status}).`);
    }

    if (!response.ok) {
      throw new Error(`Google Health request failed with HTTP ${response.status}.`);
    }

    return response.json() as Promise<unknown>;
  }

  private async exchangeCode(code: string): Promise<ProviderTokenBundle> {
    const stored = await this.readTokens();
    const clientSecret = stored.clientSecret ?? this.options.clientSecret;
    const codeVerifier = stored.authCodeVerifier;
    const body = new URLSearchParams({
      code,
      client_id: this.options.clientId!,
      redirect_uri: this.options.redirectUri!,
      grant_type: "authorization_code"
    });
    if (clientSecret) {
      body.set("client_secret", clientSecret);
    }
    if (codeVerifier) {
      body.set("code_verifier", codeVerifier);
    }

    const response = await this.fetchImpl(this.tokenUrl, {
      method: "POST",
      headers: { "Content-Type": "application/x-www-form-urlencoded" },
      body
    });

    if (!response.ok) {
      throw new Error(`Google Health token exchange failed with HTTP ${response.status}.`);
    }

    return parseTokenResponse(await response.json());
  }

  private async refreshAccessToken(): Promise<boolean> {
    const tokens = await this.readTokens();
    const refreshToken = tokens.refreshToken ?? this.options.refreshToken;
    const clientSecret = tokens.clientSecret ?? this.options.clientSecret;
    if (!refreshToken || !this.options.clientId) {
      return false;
    }
    const body = new URLSearchParams({
      refresh_token: refreshToken,
      client_id: this.options.clientId,
      grant_type: "refresh_token"
    });
    if (clientSecret) {
      body.set("client_secret", clientSecret);
    }

    const response = await this.fetchImpl(this.tokenUrl, {
      method: "POST",
      headers: { "Content-Type": "application/x-www-form-urlencoded" },
      body
    });

    if (!response.ok) {
      return false;
    }

    await this.options.tokenStore?.set(this.providerName, parseTokenResponse(await response.json()));
    return true;
  }

  private async getUsableAccessToken(): Promise<string | undefined> {
    const tokens = await this.readTokens();
    return tokens.accessToken ?? this.options.accessToken;
  }

  private async readTokens(): Promise<ProviderTokenBundle> {
    return this.options.tokenStore?.get(this.providerName) ?? {};
  }
}

function parseTokenResponse(raw: unknown): ProviderTokenBundle {
  const obj = raw && typeof raw === "object" ? raw as Record<string, unknown> : {};
  const expiresIn = typeof obj.expires_in === "number" ? obj.expires_in : undefined;
  return {
    accessToken: typeof obj.access_token === "string" ? obj.access_token : undefined,
    refreshToken: typeof obj.refresh_token === "string" ? obj.refresh_token : undefined,
    expiresAt: expiresIn ? new Date(Date.now() + expiresIn * 1000).toISOString() : undefined,
    scope: typeof obj.scope === "string" ? obj.scope : undefined
  };
}

function normalizeScopes(scopes: readonly string[] | undefined): string[] {
  return [...new Set((scopes && scopes.length > 0 ? scopes : defaultGoogleScopes()).filter(Boolean))];
}

function randomState(): string {
  return `gh_${Date.now().toString(36)}_${Math.random().toString(36).slice(2, 10)}`;
}

function createCodeVerifier(): string {
  return randomBytes(64).toString("base64url");
}

function createCodeChallenge(verifier: string): string {
  return createHash("sha256").update(verifier).digest("base64url");
}

function sanitizeError(error: unknown): string {
  const message = error instanceof Error ? error.message : String(error);
  return message
    .replace(/Bearer\s+[A-Za-z0-9._~+/-]+/gi, "Bearer [REDACTED]")
    .replace(/[A-Za-z0-9_+=/-]{40,}/g, "[REDACTED]");
}
