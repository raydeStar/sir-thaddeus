import { existsSync, unlinkSync } from 'node:fs';
import { homedir } from 'node:os';
import { join } from 'node:path';

const LOCK_PATH = join(homedir(), '.thaddeus', 'runtime.lock');

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
  if (existsSync(LOCK_PATH)) {
    try { unlinkSync(LOCK_PATH); } catch { /* ignore */ }
  }
}
