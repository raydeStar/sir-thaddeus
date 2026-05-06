import { existsSync, rmSync, unlinkSync } from 'node:fs';

/** Best-effort cleanup: kill the runtime process and remove its lock file. */
export default async function globalTeardown(): Promise<void> {
  const pid = Number(process.env.RUNTIME_PID ?? '0');
  if (pid > 0) {
    try {
      process.kill(pid);
    } catch {
      // already gone
    }
  }
  const lockPath = process.env.RUNTIME_LOCK_PATH;
  if (lockPath && existsSync(lockPath)) {
    try { unlinkSync(lockPath); } catch { /* ignore */ }
  }
  const sandbox = process.env.RUNTIME_WIKI_SANDBOX;
  if (sandbox && existsSync(sandbox)) {
    try { rmSync(sandbox, { recursive: true, force: true }); } catch { /* ignore */ }
  }
}
