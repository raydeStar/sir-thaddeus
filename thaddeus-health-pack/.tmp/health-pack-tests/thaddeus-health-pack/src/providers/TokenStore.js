import { mkdir, readFile, writeFile } from "node:fs/promises";
import { execFileSync } from "node:child_process";
import { createCipheriv, createDecipheriv, createHash, randomBytes, scryptSync } from "node:crypto";
import { dirname } from "node:path";
export class FileTokenStore {
    filePath;
    cached;
    constructor(filePath) {
        this.filePath = filePath;
    }
    async get(provider) {
        const all = await this.load();
        return decodeBundle(provider, all[provider]);
    }
    async set(provider, tokens) {
        const all = await this.load();
        const current = decodeBundle(provider, all[provider]);
        all[provider] = encodeBundle(provider, { ...current, ...dropEmpty(tokens) });
        await this.save(all);
    }
    async clear(provider) {
        const all = await this.load();
        deleteProtectedReferences(provider, all[provider]);
        delete all[provider];
        await this.save(all);
    }
    async presence(provider) {
        const bundle = await this.get(provider);
        return {
            clientSecret: Boolean(bundle.clientSecret),
            accessToken: Boolean(bundle.accessToken),
            refreshToken: Boolean(bundle.refreshToken),
            expiresAt: bundle.expiresAt,
            scope: bundle.scope
        };
    }
    async load() {
        if (this.cached) {
            return this.cached;
        }
        try {
            this.cached = JSON.parse(await readFile(this.filePath, "utf8"));
        }
        catch (error) {
            if (!isMissingFileError(error)) {
                throw error;
            }
            this.cached = {};
        }
        return this.cached;
    }
    async save(data) {
        await mkdir(dirname(this.filePath), { recursive: true });
        await writeFile(this.filePath, JSON.stringify(data, null, 2), { encoding: "utf8", mode: 0o600 });
    }
    protectionStatus() {
        return secretProtectionStatus();
    }
}
export class InMemoryTokenStore {
    data = new Map();
    get(provider) {
        return Promise.resolve({ ...(this.data.get(provider) ?? {}) });
    }
    set(provider, tokens) {
        this.data.set(provider, { ...(this.data.get(provider) ?? {}), ...dropEmpty(tokens) });
        return Promise.resolve();
    }
    clear(provider) {
        this.data.delete(provider);
        return Promise.resolve();
    }
    async presence(provider) {
        const bundle = await this.get(provider);
        return {
            clientSecret: Boolean(bundle.clientSecret),
            accessToken: Boolean(bundle.accessToken),
            refreshToken: Boolean(bundle.refreshToken),
            expiresAt: bundle.expiresAt,
            scope: bundle.scope
        };
    }
    protectionStatus() {
        return {
            backend: "memory",
            localOnly: true,
            userScoped: true,
            requiresUserKey: false,
            message: "In-memory test store. Secrets are not persisted."
        };
    }
}
const KeychainService = "SirThaddeus.HealthPack";
const SecretFields = ["clientSecret", "authCodeVerifier", "accessToken", "refreshToken"];
export function protectLocalSecret(scope, value) {
    const protectedValue = protect(scope, "accessToken", value);
    if (!protectedValue) {
        throw new Error("Local secret protection returned no value.");
    }
    return protectedValue;
}
export function unprotectLocalSecret(scope, protectedValue) {
    const value = unprotect(scope, "accessToken", protectedValue);
    if (!value) {
        throw new Error("Local secret unprotection returned no value.");
    }
    return value;
}
function encodeBundle(provider, bundle) {
    return {
        clientSecret: protect(provider, "clientSecret", bundle.clientSecret),
        authCodeVerifier: protect(provider, "authCodeVerifier", bundle.authCodeVerifier),
        accessToken: protect(provider, "accessToken", bundle.accessToken),
        refreshToken: protect(provider, "refreshToken", bundle.refreshToken),
        expiresAt: bundle.expiresAt,
        scope: bundle.scope
    };
}
function decodeBundle(provider, bundle) {
    if (!bundle) {
        return {};
    }
    return {
        clientSecret: unprotect(provider, "clientSecret", bundle.clientSecret),
        authCodeVerifier: unprotect(provider, "authCodeVerifier", bundle.authCodeVerifier),
        accessToken: unprotect(provider, "accessToken", bundle.accessToken),
        refreshToken: unprotect(provider, "refreshToken", bundle.refreshToken),
        expiresAt: bundle.expiresAt,
        scope: bundle.scope
    };
}
function protect(provider, field, value) {
    if (!value) {
        return undefined;
    }
    if (process.platform === "win32") {
        return `dpapi:${runDpapi("protect", value)}`;
    }
    if (process.platform === "darwin") {
        const account = secretAccount(provider, field);
        runSecurity(["add-generic-password", "-a", account, "-s", KeychainService, "-w", value, "-U"]);
        return `keychain:${Buffer.from(account, "utf8").toString("base64url")}`;
    }
    if (process.platform === "linux" && commandExists("secret-tool")) {
        runSecretTool(["store", "--label", `${KeychainService} ${provider} ${field}`, "app", "sir-thaddeus", "component", "health-pack", "provider", provider, "field", field], value);
        return `secret-service:${Buffer.from(`${provider}:${field}`, "utf8").toString("base64url")}`;
    }
    const userKey = readFallbackUserKey();
    if (userKey) {
        return `enc:v1:${encryptWithUserKey(userKey, value)}`;
    }
    throw new Error("No local OS secret store is available. Configure THADDEUS_HEALTH_SECRET_KEY to enable encrypted-file fallback.");
}
function unprotect(provider, field, value) {
    if (!value) {
        return undefined;
    }
    if (value.startsWith("dpapi:")) {
        if (process.platform !== "win32") {
            throw new Error("DPAPI-protected Health Pack tokens can only be read by the same Windows user profile.");
        }
        return runDpapi("unprotect", value.slice("dpapi:".length));
    }
    if (value.startsWith("keychain:")) {
        const account = Buffer.from(value.slice("keychain:".length), "base64url").toString("utf8");
        return runSecurity(["find-generic-password", "-a", account, "-s", KeychainService, "-w"]);
    }
    if (value.startsWith("secret-service:")) {
        return runSecretTool(["lookup", "app", "sir-thaddeus", "component", "health-pack", "provider", provider, "field", field]);
    }
    if (value.startsWith("enc:v1:")) {
        const userKey = readFallbackUserKey();
        if (!userKey) {
            throw new Error("Encrypted Health Pack token fallback requires THADDEUS_HEALTH_SECRET_KEY.");
        }
        return decryptWithUserKey(userKey, value.slice("enc:v1:".length));
    }
    if (value.startsWith("b64:")) {
        return Buffer.from(value.slice("b64:".length), "base64url").toString("utf8");
    }
    return Buffer.from(value, "base64url").toString("utf8");
}
function runDpapi(mode, value) {
    const script = mode === "protect"
        ? "Add-Type -AssemblyName System.Security;$bytes=[Text.Encoding]::UTF8.GetBytes($env:THADDEUS_DPAPI_VALUE);$out=[Security.Cryptography.ProtectedData]::Protect($bytes,$null,[Security.Cryptography.DataProtectionScope]::CurrentUser);[Convert]::ToBase64String($out)"
        : "Add-Type -AssemblyName System.Security;$bytes=[Convert]::FromBase64String($env:THADDEUS_DPAPI_VALUE);$out=[Security.Cryptography.ProtectedData]::Unprotect($bytes,$null,[Security.Cryptography.DataProtectionScope]::CurrentUser);[Text.Encoding]::UTF8.GetString($out)";
    return execFileSync("powershell.exe", [
        "-NoProfile",
        "-NonInteractive",
        "-ExecutionPolicy",
        "Bypass",
        "-Command",
        script
    ], {
        encoding: "utf8",
        env: {
            ...process.env,
            THADDEUS_DPAPI_VALUE: value
        },
        windowsHide: true,
        stdio: ["ignore", "pipe", "pipe"]
    }).trim();
}
function deleteProtectedReferences(provider, bundle) {
    if (!bundle) {
        return;
    }
    for (const field of SecretFields) {
        const value = bundle[field];
        if (!value) {
            continue;
        }
        try {
            if (value.startsWith("keychain:")) {
                const account = Buffer.from(value.slice("keychain:".length), "base64url").toString("utf8");
                runSecurity(["delete-generic-password", "-a", account, "-s", KeychainService]);
            }
            else if (value.startsWith("secret-service:")) {
                runSecretTool(["clear", "app", "sir-thaddeus", "component", "health-pack", "provider", provider, "field", field]);
            }
        }
        catch {
            // Best-effort cleanup. Losing the index still makes the secret unreachable through Health Pack.
        }
    }
}
export function secretProtectionStatus() {
    if (process.platform === "win32") {
        return {
            backend: "windows-dpapi",
            localOnly: true,
            userScoped: true,
            requiresUserKey: false,
            message: "Secrets are encrypted with Windows DPAPI for the current user profile."
        };
    }
    if (process.platform === "darwin") {
        return {
            backend: "macos-keychain",
            localOnly: true,
            userScoped: true,
            requiresUserKey: false,
            message: "Secrets are stored as references in the user's macOS Keychain."
        };
    }
    if (process.platform === "linux" && commandExists("secret-tool")) {
        return {
            backend: "linux-secret-service",
            localOnly: true,
            userScoped: true,
            requiresUserKey: false,
            message: "Secrets are stored as references in the user's Secret Service keyring."
        };
    }
    if (readFallbackUserKey()) {
        return {
            backend: "encrypted-file",
            localOnly: true,
            userScoped: false,
            requiresUserKey: true,
            message: "Secrets are encrypted in the local token file with the user-supplied fallback key."
        };
    }
    return {
        backend: "unavailable",
        localOnly: true,
        userScoped: false,
        requiresUserKey: true,
        message: "No OS secret store was detected. Set THADDEUS_HEALTH_SECRET_KEY to enable encrypted-file fallback."
    };
}
function secretAccount(provider, field) {
    return `${KeychainService}.${provider}.${field}`;
}
function runSecurity(args) {
    return execFileSync("security", args, {
        encoding: "utf8",
        stdio: ["ignore", "pipe", "pipe"]
    }).trim();
}
function runSecretTool(args, stdin) {
    return execFileSync("secret-tool", args, {
        encoding: "utf8",
        input: stdin,
        stdio: [stdin === undefined ? "ignore" : "pipe", "pipe", "pipe"]
    }).trim();
}
function commandExists(command) {
    try {
        if (process.platform === "win32") {
            execFileSync("where.exe", [command], { stdio: "ignore" });
        }
        else {
            execFileSync("sh", ["-c", `command -v ${shellQuote(command)}`], { stdio: "ignore" });
        }
        return true;
    }
    catch {
        return false;
    }
}
function shellQuote(value) {
    return `'${value.replace(/'/g, "'\\''")}'`;
}
function readFallbackUserKey() {
    const value = process.env.THADDEUS_HEALTH_SECRET_KEY ?? process.env.SIR_THADDEUS_SECRET_KEY;
    return typeof value === "string" && value.length >= 16 ? value : undefined;
}
function encryptWithUserKey(userKey, value) {
    const salt = randomBytes(16);
    const iv = randomBytes(12);
    const key = deriveKey(userKey, salt);
    const cipher = createCipheriv("aes-256-gcm", key, iv);
    const ciphertext = Buffer.concat([cipher.update(value, "utf8"), cipher.final()]);
    const tag = cipher.getAuthTag();
    return Buffer.concat([salt, iv, tag, ciphertext]).toString("base64url");
}
function decryptWithUserKey(userKey, payload) {
    const raw = Buffer.from(payload, "base64url");
    const salt = raw.subarray(0, 16);
    const iv = raw.subarray(16, 28);
    const tag = raw.subarray(28, 44);
    const ciphertext = raw.subarray(44);
    const key = deriveKey(userKey, salt);
    const decipher = createDecipheriv("aes-256-gcm", key, iv);
    decipher.setAuthTag(tag);
    return Buffer.concat([decipher.update(ciphertext), decipher.final()]).toString("utf8");
}
function deriveKey(userKey, salt) {
    const pepper = createHash("sha256").update("sir-thaddeus-health-pack-secret-store-v1").digest();
    return scryptSync(userKey, Buffer.concat([salt, pepper]), 32);
}
function dropEmpty(tokens) {
    return Object.fromEntries(Object.entries(tokens).filter(([, value]) => typeof value === "string" ? value.trim().length > 0 : value !== undefined));
}
function isMissingFileError(error) {
    return typeof error === "object" &&
        error !== null &&
        "code" in error &&
        error.code === "ENOENT";
}
