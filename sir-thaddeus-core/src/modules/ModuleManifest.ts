export type ModuleId = string;
export type CapabilityName = string;
export type ToolName = CapabilityName;
export type JobName = CapabilityName;
export type HookName = CapabilityName;
export type MemoryNamespace = string;
export type SettingName = string;

export interface ExternalAccountPermission {
  provider: string;
  scopes: string[];
}

export interface MemoryPermissionSet {
  read?: MemoryNamespace[];
  write?: MemoryNamespace[];
}

export interface ModulePermissionSet {
  externalAccounts?: ExternalAccountPermission[];
  memory?: MemoryPermissionSet;
  notifications?: string[];
}

export type ModuleSettingType = "string" | "number" | "boolean" | "json";

export interface ModuleSettingDefinition {
  key: SettingName;
  type: ModuleSettingType;
  label?: string;
  description?: string;
  defaultValue?: unknown;
  required?: boolean;
}

export interface ModuleManifest {
  id: ModuleId;
  name: string;
  version: string;
  description?: string;
  permissions?: ModulePermissionSet;
  tools?: ToolName[];
  jobs?: JobName[];
  hooks?: HookName[];
  settings?: ModuleSettingDefinition[];
  memoryNamespaces?: MemoryNamespace[];
  execution?: ModuleExecution;
}

export interface ModuleExecution {
  type: "stdio";
  command: string;
  args?: string[];
  cwd?: string;
  env?: Record<string, string>;
}

export interface NormalizedModuleManifest extends ModuleManifest {
  permissions: Required<Pick<ModulePermissionSet, "externalAccounts" | "notifications">> & {
    memory: Required<MemoryPermissionSet>;
  };
  tools: ToolName[];
  jobs: JobName[];
  hooks: HookName[];
  settings: ModuleSettingDefinition[];
  memoryNamespaces: MemoryNamespace[];
}

export function normalizeModuleManifest(manifest: ModuleManifest): NormalizedModuleManifest {
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

export function validateModuleManifest(manifest: ModuleManifest): void {
  requireNonEmpty("id", manifest.id);
  requireNonEmpty("name", manifest.name);
  requireNonEmpty("version", manifest.version);

  requireUnique("tools", manifest.tools ?? []);
  requireUnique("jobs", manifest.jobs ?? []);
  requireUnique("hooks", manifest.hooks ?? []);
  requireUnique("memoryNamespaces", manifest.memoryNamespaces ?? []);
  requireUnique("memory.read", manifest.permissions?.memory?.read ?? []);
  requireUnique("memory.write", manifest.permissions?.memory?.write ?? []);
  requireUnique(
    "externalAccounts.provider",
    (manifest.permissions?.externalAccounts ?? []).map((account) => account.provider)
  );

  for (const account of manifest.permissions?.externalAccounts ?? []) {
    requireNonEmpty("externalAccounts.provider", account.provider);
    if (!Array.isArray(account.scopes)) {
      throw new Error(`Module '${manifest.id}' external account '${account.provider}' must declare scopes.`);
    }
    requireUnique(`externalAccounts.${account.provider}.scopes`, account.scopes);
  }

  for (const setting of manifest.settings ?? []) {
    requireNonEmpty("settings.key", setting.key);
    requireUnique(
      "settings",
      (manifest.settings ?? []).map((item) => item.key)
    );
  }
}

function requireNonEmpty(field: string, value: string | undefined): void {
  if (typeof value !== "string" || value.trim().length === 0) {
    throw new Error(`Module manifest requires a non-empty '${field}'.`);
  }
}

function requireUnique(field: string, values: string[]): void {
  const seen = new Set<string>();
  for (const value of values) {
    requireNonEmpty(field, value);
    if (seen.has(value)) {
      throw new Error(`Module manifest '${field}' contains duplicate value '${value}'.`);
    }
    seen.add(value);
  }
}
