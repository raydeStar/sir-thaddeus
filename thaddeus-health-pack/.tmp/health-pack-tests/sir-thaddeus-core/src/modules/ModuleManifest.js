export function normalizeModuleManifest(manifest) {
    validateModuleManifest(manifest);
    return {
        ...manifest,
        permissions: {
            externalAccounts: [...(manifest.permissions?.externalAccounts ?? [])],
            memory: {
                read: [...(manifest.permissions?.memory?.read ?? [])],
                write: [...(manifest.permissions?.memory?.write ?? [])]
            },
            notifications: [...(manifest.permissions?.notifications ?? [])]
        },
        tools: [...(manifest.tools ?? [])],
        jobs: [...(manifest.jobs ?? [])],
        hooks: [...(manifest.hooks ?? [])],
        settings: [...(manifest.settings ?? [])],
        memoryNamespaces: [...(manifest.memoryNamespaces ?? [])]
    };
}
export function validateModuleManifest(manifest) {
    requireNonEmpty("id", manifest.id);
    requireNonEmpty("name", manifest.name);
    requireNonEmpty("version", manifest.version);
    requireUnique("tools", manifest.tools ?? []);
    requireUnique("jobs", manifest.jobs ?? []);
    requireUnique("hooks", manifest.hooks ?? []);
    requireUnique("memoryNamespaces", manifest.memoryNamespaces ?? []);
    requireUnique("memory.read", manifest.permissions?.memory?.read ?? []);
    requireUnique("memory.write", manifest.permissions?.memory?.write ?? []);
    requireUnique("externalAccounts.provider", (manifest.permissions?.externalAccounts ?? []).map((account) => account.provider));
    for (const account of manifest.permissions?.externalAccounts ?? []) {
        requireNonEmpty("externalAccounts.provider", account.provider);
        if (!Array.isArray(account.scopes)) {
            throw new Error(`Module '${manifest.id}' external account '${account.provider}' must declare scopes.`);
        }
        requireUnique(`externalAccounts.${account.provider}.scopes`, account.scopes);
    }
    for (const setting of manifest.settings ?? []) {
        requireNonEmpty("settings.key", setting.key);
        requireUnique("settings", (manifest.settings ?? []).map((item) => item.key));
    }
}
function requireNonEmpty(field, value) {
    if (typeof value !== "string" || value.trim().length === 0) {
        throw new Error(`Module manifest requires a non-empty '${field}'.`);
    }
}
function requireUnique(field, values) {
    const seen = new Set();
    for (const value of values) {
        requireNonEmpty(field, value);
        if (seen.has(value)) {
            throw new Error(`Module manifest '${field}' contains duplicate value '${value}'.`);
        }
        seen.add(value);
    }
}
