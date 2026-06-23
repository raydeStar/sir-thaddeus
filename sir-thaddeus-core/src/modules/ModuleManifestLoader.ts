import { readFile } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import { ModuleManifest, normalizeModuleManifest, NormalizedModuleManifest } from "./ModuleManifest.js";

export interface LoadedModuleManifest {
  manifest: NormalizedModuleManifest;
  manifestPath: string;
  moduleRoot: string;
}

export async function loadModuleManifest(manifestPath: string): Promise<LoadedModuleManifest> {
  const resolvedPath = resolve(manifestPath);
  const raw = await readFile(resolvedPath, "utf8");
  const parsed = JSON.parse(raw) as ModuleManifest;
  const manifest = normalizeModuleManifest(parsed);

  return {
    manifest,
    manifestPath: resolvedPath,
    moduleRoot: dirname(resolvedPath)
  };
}
