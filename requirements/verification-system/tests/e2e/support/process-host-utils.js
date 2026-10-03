/**
 * process-host-utils.js — Pure helpers for ProcessHost: child lifecycle,
 * socket/PID/process-tree checks, listen-port parsing.
 *
 * Side-effect-free functions live here; the main class file imports them.
 */

import { execFileSync, spawn } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { getDescendantPids } from "./process-host-checks.js";
import { terminateTree } from "./process-lifecycle.js";
import { recordSpawn, recordExit } from "./spawn-ledger.js";
import { SIGTERM_GRACE_MS, SIGKILL_GRACE_MS } from "./time-budget.js";

const REPO_ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../../../../..");

/** Prefer repo-local bin so CI `npm ci` can run ProcessHost without a global install.
 * If local binary cannot be executed (e.g. Linux ELF on Darwin), walk PATH for the
 * first runnable absolute candidate — bare `"opencode"` would re-hit the broken
 * `node_modules/.bin` entry that npm scripts prepend to PATH.
 */
function defaultOpencodeBin() {
  const local = path.join(REPO_ROOT, "node_modules", ".bin", "opencode");
  const candidates = [];
  if (fs.existsSync(local)) candidates.push(local);
  for (const dir of (process.env.PATH || "").split(path.delimiter)) {
    if (!dir) continue;
    const candidate = path.join(dir, process.platform === "win32" ? "opencode.cmd" : "opencode");
    if (candidate !== local && fs.existsSync(candidate)) candidates.push(candidate);
  }
  for (const candidate of candidates) {
    try {
      execFileSync(candidate, ["--version"], { stdio: "ignore" });
      return candidate;
    } catch {
      /* try next */
    }
  }
  return "opencode";
}

export const OPENCODE_BIN = process.env.OPENCODE_BIN || defaultOpencodeBin();

const STDOUT_RING_MAX = 100;
const activeChildPids = new Set();

/** In-process OpenCode serve spawn counter (G4R §2: exactly one lifetime). */
let opencodeServeSpawnCount = 0;

/** How many times `spawnOpencodeServe` succeeded in creating a child with a pid. */
export function getOpencodeSpawnCount() {
  return opencodeServeSpawnCount;
}

/** Test-only reset between isolated harness cases (not used by Long Stroke). */
export function resetOpencodeSpawnCount() {
  opencodeServeSpawnCount = 0;
}

export const READY_POLL_INTERVAL_MS = 100;
export const READY_POLL_MAX_TRIES = 50;

function cleanupAllActiveChildren() {
  for (const pid of activeChildPids) {
    try {
      if (process.platform !== "win32") {
        process.kill(-pid, "SIGKILL");
      }
    } catch {}
    try { process.kill(pid, "SIGKILL"); } catch {}
  }
  activeChildPids.clear();
}

process.on("exit", cleanupAllActiveChildren);
process.on("SIGINT", () => { cleanupAllActiveChildren(); process.exit(130); });
process.on("SIGTERM", () => { cleanupAllActiveChildren(); process.exit(143); });

export function parseListenPort(listenLine) {
  const m = listenLine.match(/http:\/\/127\.0\.0\.1:(\d+)/)
    || listenLine.match(/http:\/\/localhost:(\d+)/)
    || listenLine.match(/:(\d+)/);
  return m ? Number(m[1]) : 0;
}

export function pidIsAlive(pid) {
  try {
    process.kill(pid, 0);
    return true;
  } catch (err) {
    if (err.code === "ESRCH") return false;
    if (err.code === "EPERM") return true;
    return false;
  }
}

export function ringPush(buffer, s) {
  buffer.push(s);
  if (buffer.length > STDOUT_RING_MAX) buffer.shift();
}

export async function terminateChild(child, termMs = SIGTERM_GRACE_MS, killMs = SIGKILL_GRACE_MS) {
  const pid = child?.pid;
  if (!pid) return;
  activeChildPids.delete(pid);

  try {
    const descendants = await getDescendantPids(pid);
    for (const dpid of descendants) {
      try { process.kill(dpid, "SIGKILL"); } catch {}
    }
  } catch {}

  try {
    await terminateTree(child, { termGraceMs: termMs, killGraceMs: killMs });
  } catch (err) {
    // terminateTree already sent SIGTERM then SIGKILL to the whole process group
    // and still found survivors. Under heavy parallel load a descendant can be
    // mid-fork when the group kill runs, escaping the group; one more SIGKILL
    // aimed at the group (plus the leader) closes that race. Not a budget bump —
    // SIGKILL is unconditional, and the caller's assertNoLeak still verifies the
    // port is actually gone afterwards.
    console.error(`[ProcessHost] terminateTree error: ${err.message}; retrying SIGKILL`);
    try { process.kill(-pid, "SIGKILL"); } catch {}
    try { process.kill(pid, "SIGKILL"); } catch {}
  }
}

export async function initGitWorkspace(workDir) {
  const gitDir = path.join(workDir, ".git");
  if (fs.existsSync(gitDir)) return;
  try {
    const { execSync } = await import("node:child_process");
    execSync("git init", { cwd: workDir, stdio: "ignore" });
    execSync("git config user.email test@example.com", { cwd: workDir, stdio: "ignore" });
    execSync("git config user.name test", { cwd: workDir, stdio: "ignore" });
    // Diagnostic bridge + local runtime dirs must never trip Orchestrator IsDirty.
    const excludePath = path.join(gitDir, "info", "exclude");
    fs.mkdirSync(path.dirname(excludePath), { recursive: true });
    fs.appendFileSync(
      excludePath,
      "\n# wanxiangshu e2e / diagnostic non-authoritative paths\n.wanxiangshu/\n",
      "utf8",
    );
    // Stage any fixture files already written by setupScenario, then commit.
    // --allow-empty keeps an empty initial commit when the canary has no files.
    execSync("git add -A", { cwd: workDir, stdio: "ignore" });
    execSync("git commit --allow-empty -m init", { cwd: workDir, stdio: "ignore" });
  } catch {
    // Non-fatal.
  }
}

export function spawnOpencodeServe(workDir, env, hooks) {
  const child = spawn(
    OPENCODE_BIN,
    ["serve", "--port", "0", "--hostname", "127.0.0.1"],
    {
      cwd: workDir,
      env,
      stdio: ["pipe", "pipe", "pipe"],
      windowsHide: true,
      detached: process.platform !== "win32",
    },
  );
  if (child.pid) {
    activeChildPids.add(child.pid);
    opencodeServeSpawnCount += 1;
    recordSpawn(child.pid, `opencode serve ${workDir}`);
  }
  child.stdout.on("data", (chunk) => hooks.onStdoutChunk(chunk.toString()));
  child.stderr.on("data", (chunk) => hooks.onStderrChunk(chunk.toString()));
  child.on("exit", (code, signal) => {
    if (child.pid) {
      activeChildPids.delete(child.pid);
      recordExit(child.pid);
    }
    hooks.onExit(code, signal);
  });
  return child;
}
