import { runtimeFetch, readRuntimeMetadata, parseRuntimeJson } from './runtime';

export type ModuleApprovalStatus = 'Pending' | 'Approved' | 'Denied';

export interface ModuleExecutionDefinition {
  type: string;
  command: string;
  args: string[];
  cwd?: string | null;
  envKeys: string[];
}

export interface ModuleAuditEvent {
  id: string;
  moduleId: string;
  action: string;
  result: string;
  at: string;
  message?: string | null;
  toolName?: string | null;
}

export interface ModuleTool {
  name: string;
  description?: string | null;
  inputSchema?: unknown;
  canInvokeManually: boolean;
}

export interface ModuleSummary {
  id: string;
  name: string;
  version: string;
  description?: string | null;
  manifestPath: string;
  status: string;
  approvalStatus: ModuleApprovalStatus;
  disabled: boolean;
  permissionCount: number;
  toolCount: number;
  lastStatusCheck?: string | null;
  lastInvocation?: string | null;
  lastError?: string | null;
}

export interface ModuleDetail extends ModuleSummary {
  requestedPermissions?: Record<string, unknown> | null;
  tools: ModuleTool[];
  jobs: string[];
  hooks: string[];
  memoryNamespaces: string[];
  execution?: ModuleExecutionDefinition | null;
  recentAuditEvents: ModuleAuditEvent[];
}

export interface ModuleListResponse {
  modules: ModuleSummary[];
}

export interface ModuleInvokeResponse {
  moduleId: string;
  toolName: string;
  ok: boolean;
  content: string;
  json?: unknown;
  invokedAt: string;
}

export interface ModuleStatusResponse {
  moduleId: string;
  status: string;
  checkedAt: string;
  lastError?: string | null;
  providerStatus?: ModuleInvokeResponse | null;
}

const token = () => readRuntimeMetadata().token;

export async function listModules(): Promise<ModuleSummary[]> {
  const res = await runtimeFetch(token(), '/api/modules');
  return (await parseRuntimeJson<ModuleListResponse>(res)).modules;
}

export async function getModule(moduleId: string): Promise<ModuleDetail> {
  const res = await runtimeFetch(token(), `/api/modules/${encodeURIComponent(moduleId)}`);
  return parseRuntimeJson<ModuleDetail>(res);
}

export async function approveModule(moduleId: string): Promise<ModuleDetail> {
  return mutateModule(moduleId, 'approve');
}

export async function denyModule(moduleId: string): Promise<ModuleDetail> {
  return mutateModule(moduleId, 'deny');
}

export async function disableModule(moduleId: string): Promise<ModuleDetail> {
  return mutateModule(moduleId, 'disable');
}

export async function enableModule(moduleId: string): Promise<ModuleDetail> {
  return mutateModule(moduleId, 'enable');
}

export async function checkModuleStatus(moduleId: string): Promise<ModuleStatusResponse> {
  const res = await runtimeFetch(token(), `/api/modules/${encodeURIComponent(moduleId)}/status`, {
    method: 'POST',
  });
  return parseRuntimeJson<ModuleStatusResponse>(res);
}

export async function invokeModuleTool(
  moduleId: string,
  toolName: string,
  args?: Record<string, unknown>,
): Promise<ModuleInvokeResponse> {
  const res = await runtimeFetch(
    token(),
    `/api/modules/${encodeURIComponent(moduleId)}/tools/${encodeURIComponent(toolName)}/invoke`,
    {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ arguments: args ?? {} }),
    },
  );
  return parseRuntimeJson<ModuleInvokeResponse>(res);
}

async function mutateModule(moduleId: string, action: string): Promise<ModuleDetail> {
  const res = await runtimeFetch(token(), `/api/modules/${encodeURIComponent(moduleId)}/${action}`, {
    method: 'POST',
  });
  return parseRuntimeJson<ModuleDetail>(res);
}
