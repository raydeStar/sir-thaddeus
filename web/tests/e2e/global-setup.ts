import { spawn, spawnSync, ChildProcess } from 'node:child_process';
import { existsSync, readFileSync, unlinkSync } from 'node:fs';
import { homedir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

interface LockFileShape {
  pid: number;
  port: number;
  token: string;
  version: string;
  ipcEndpoint: string;
  startedAt: string;
}

const __dirname_resolved = dirname(fileURLToPath(import.meta.url));
const LOCK_PATH = join(homedir(), '.thaddeus', 'runtime.lock');
const REPO_ROOT = join(__dirname_resolved, '..', '..', '..');
const WEB_ROOT = join(REPO_ROOT, 'web');
const RUNTIME_PROJECT = join(REPO_ROOT, 'src', 'Thaddeus.Runtime', 'Thaddeus.Runtime.csproj');

let runtime: ChildProcess | null = null;

/** Sleeps for the given milliseconds. */
function sleep(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

/** Polls until the runtime lock file appears or the timeout elapses. */
async function waitForLockFile(timeoutMs: number): Promise<LockFileShape> {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if (existsSync(LOCK_PATH)) {
      try {
        const raw = readFileSync(LOCK_PATH, 'utf8');
        const parsed = JSON.parse(raw) as LockFileShape;
        if (parsed.port > 0 && parsed.token) return parsed;
      } catch {
        // partial write; retry
      }
    }
    await sleep(150);
  }
  throw new Error(`Runtime did not write a lock file within ${timeoutMs}ms.`);
}

function runOrThrow(command: string, args: string[], cwd: string): void {
  const result = spawnSync(command, args, {
    cwd,
    stdio: 'inherit',
    shell: process.platform === 'win32',
  });

  if (result.status !== 0) {
    const errorMessage = result.error ? ` (${result.error.message})` : '';
    throw new Error(
      `[playwright] ${command} ${args.join(' ')} failed with exit code ${result.status ?? -1}${errorMessage}`,
    );
  }
}

export default async function globalSetup(): Promise<void> {
  // Pre-clean any stale lock from a previous crashed run.
  if (existsSync(LOCK_PATH)) {
    try { unlinkSync(LOCK_PATH); } catch { /* ignore */ }
  }

  // The runtime serves built assets from src/Thaddeus.Runtime/wwwroot, not
  // raw web/src. Build the SPA first, then rebuild the runtime so the
  // synced wwwroot bundle and Release binaries match the current sources.
  runOrThrow('npm', ['run', 'build'], WEB_ROOT);
  runOrThrow('dotnet', ['build', RUNTIME_PROJECT, '-c', 'Release', '--no-restore'], REPO_ROOT);

  runtime = spawn(
    'dotnet',
    ['run', '--project', RUNTIME_PROJECT, '-c', 'Release', '--no-build', '--', '--test-mode'],
    {
      cwd: REPO_ROOT,
      stdio: ['ignore', 'inherit', 'inherit'],
      shell: false,
    },
  );

  runtime.on('exit', (code) => {
    if (code !== null && code !== 0) {
      // eslint-disable-next-line no-console
      console.error(`[playwright] runtime exited with code ${code}`);
    }
  });

  const lock = await waitForLockFile(45_000);
  process.env.RUNTIME_BASE_URL = `http://127.0.0.1:${lock.port}`;
  process.env.RUNTIME_TOKEN = lock.token;
  process.env.RUNTIME_PID = String(runtime.pid ?? 0);
  process.env.RUNTIME_VERSION = lock.version;

  // eslint-disable-next-line no-console
  console.log(`[playwright] runtime ready at ${process.env.RUNTIME_BASE_URL} (pid=${runtime.pid})`);
}
