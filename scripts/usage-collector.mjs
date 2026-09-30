import fs from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath, pathToFileURL } from 'node:url';

export const WEEK_MS = 7 * 24 * 60 * 60 * 1000;
const numericFields = ['inputTokens', 'outputTokens', 'cacheCreationTokens', 'cacheReadTokens', 'totalTokens', 'totalCost'];
const uuidPattern = /[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/ig;

export function readJson(filename, fallback) {
  if (!fs.existsSync(filename)) return fallback;
  const bytes = fs.readFileSync(filename);
  // Windows PowerShell redirection writes UTF-16LE, including the user's seed files.
  const encoding = bytes[0] === 0xff && bytes[1] === 0xfe ? 'utf16le' : 'utf8';
  return JSON.parse(bytes.toString(encoding).replace(/^\uFEFF/, ''));
}

export function atomicJson(filename, value) {
  if (!fs.existsSync(path.dirname(filename))) fs.mkdirSync(path.dirname(filename), { recursive: true });
  const temporary = `${filename}.${process.pid}.tmp`;
  try {
    fs.writeFileSync(temporary, JSON.stringify(value, null, 2) + '\n', 'utf8');
    fs.renameSync(temporary, filename);
  } finally {
    if (fs.existsSync(temporary)) fs.unlinkSync(temporary);
  }
}

export function sessionId(row) {
  const candidate = String(row.sessionId ?? row.id ?? row.period ?? '');
  if (!candidate) throw new Error('Session has no stable ID.');
  if (row.agent === 'codex') return (candidate.match(uuidPattern)?.at(-1) ?? candidate).toLowerCase();
  return candidate;
}

export function mergeRows(existing, incoming, kind, observedAt) {
  const rows = new Map();
  const keyFor = row => {
    const agent = row.agent ?? 'all';
    const identity = kind === 'session' ? sessionId(row) : row.period ?? row.date;
    if (!identity || (kind === 'daily' && !/^\d{4}-\d{2}-\d{2}$/.test(identity))) throw new Error(`Invalid ${kind} identity.`);
    return `${agent}|${identity}`;
  };
  for (const row of existing) rows.set(keyFor(row), row);
  for (const row of incoming) {
    if (!row || typeof row !== 'object') throw new Error('Invalid report row.');
    for (const name of numericFields) {
      if (row[name] !== undefined && (!Number.isFinite(row[name]) || row[name] < 0)) throw new Error(`Invalid ${name}.`);
    }
    const key = keyFor(row);
    const previous = rows.get(key);
    // Each report is a cumulative snapshot, never an additive transaction.
    // Older imports cannot overwrite a newer observation.
    if (previous?.observedAt && Date.parse(previous.observedAt) > Date.parse(observedAt)) continue;
    rows.set(key, { ...row, ...(kind === 'session' ? { id: sessionId(row), ...(row.displayName ?? previous?.displayName ? { displayName: row.displayName ?? previous.displayName } : {}) } : {}),
      firstSeenAt: previous?.firstSeenAt ?? observedAt, observedAt });
  }
  return [...rows.values()].sort((a, b) => keyFor(a).localeCompare(keyFor(b)));
}

export function totals(rows) {
  return Object.fromEntries(numericFields.map(name => [name, rows.reduce((sum, row) => sum + (row[name] ?? 0), 0)]));
}

function reportRows(report, kind) {
  const rows = kind === 'session' ? report.session ?? report.sessions : report.daily;
  if (!Array.isArray(rows)) throw new Error(`Expected ${kind} array in ccusage JSON.`);
  return rows;
}

function mergeReport(filename, incoming, kind, observedAt) {
  const previous = readJson(filename, { schemaVersion: 1, [kind]: [] });
  if (previous.schemaVersion !== 1 || !Array.isArray(previous[kind])) throw new Error(`Unsupported aggregate: ${filename}`);
  const rows = mergeRows(previous[kind], incoming, kind, observedAt);
  const result = { schemaVersion: 1, kind, generatedAt: observedAt, mergePolicy: 'latest snapshot per agent and stable identity; missing records retained',
    costBasis: 'API-equivalent USD estimate, not subscription billing', [kind]: rows, totals: totals(rows) };
  atomicJson(filename, result);
  return result;
}

function loadTitles(codexHome, ids) {
  const names = new Map();
  const index = path.join(codexHome, 'session_index.jsonl');
  if (fs.existsSync(index)) {
    for (const line of fs.readFileSync(index, 'utf8').split(/\r?\n/)) {
      try {
        const item = JSON.parse(line);
        if (item.id && item.thread_name) names.set(item.id.toLowerCase(), item.thread_name);
      } catch { /* A concurrently written final line can be incomplete. */ }
    }
  }
  // Read only IDs/titles, never conversation bodies or authentication.
  const database = path.join(codexHome, 'state_5.sqlite');
  if (fs.existsSync(database) && ids.length) {
    try {
      const { DatabaseSync } = process.getBuiltinModule('node:sqlite');
      const db = new DatabaseSync(database, { readOnly: true });
      try {
        const query = db.prepare('SELECT name, agent_nickname FROM threads WHERE id = ?');
        for (const id of ids) {
          const item = query.get(id);
          const title = item?.name ?? (item?.agent_nickname ? `${item.agent_nickname} (subagent)` : null);
          if (typeof title === 'string' && title.trim()) names.set(id, title);
        }
      } finally { db.close(); }
    } catch { /* CLI-only installations may not have the desktop database. */ }
  }
  return names;
}

function hasMissingPricing(row) {
  return Boolean(row.missingPricing || row.metadata?.missingPricing ||
    row.modelBreakdowns?.some(model => model.missingPricing) || Object.values(row.models ?? {}).some(model => model.missingPricing));
}

export function makeHover(daily, sessions, generatedAt, names = new Map()) {
  const codexSessions = sessions.filter(row => row.agent === 'codex');
  const recentSessions = codexSessions.map(row => ({ id: sessionId(row),
    displayName: names.get(sessionId(row)) ?? row.displayName ?? row.metadata?.title ?? row.sessionName ?? `Session ${sessionId(row).slice(0, 8)}`,
    lastActivity: row.metadata?.lastActivity ?? row.lastActivity ?? row.observedAt,
    costUSD: row.totalCost ?? row.costUSD ?? 0, totalTokens: row.totalTokens ?? 0,
    missingPricing: hasMissingPricing(row) }))
    .sort((a, b) => Date.parse(b.lastActivity) - Date.parse(a.lastActivity)).slice(0, 5);
  const codexDaily = daily.filter(row => row.agent === 'codex').map(row => ({
    date: row.period ?? row.date, totalCost: row.totalCost ?? 0, totalTokens: row.totalTokens ?? 0, missingPricing: hasMissingPricing(row) }));
  return { schemaVersion: 1, provider: 'codex', generatedAt, costBasis: 'API-equivalent USD estimate; not billed spending',
    daily: codexDaily, totalCost: totals(codexDaily).totalCost, recentSessions };
}

export function weeklyDue(state, now = Date.now()) {
  if (!state.lastAttemptAt) return true;
  const last = Date.parse(state.lastAttemptAt);
  if (!Number.isFinite(last)) throw new Error('Invalid lastAttemptAt; refusing an unguarded collection.');
  return now - last >= WEEK_MS;
}

function childEnvironment(config) {
  const environment = { ...process.env };
  const cache = path.join(config.dataDirectory, 'package-cache');
  environment.PNPM_HOME = path.join(cache, 'home');
  environment.XDG_CACHE_HOME = path.join(cache, 'cache');
  environment.XDG_STATE_HOME = path.join(cache, 'state');
  environment.npm_config_cache = path.join(cache, 'npm');
  environment.npm_config_store_dir = path.join(cache, 'store');
  environment.npm_config_cache_dir = path.join(cache, 'cache');
  environment.npm_config_state_dir = path.join(cache, 'state');
  environment.TEMP = path.join(cache, 'tmp');
  environment.TMP = environment.TEMP;
  fs.mkdirSync(environment.TEMP, { recursive: true });
  // Disable discovery outside explicitly configured roots for every source.
  const sources = ['CLAUDE_CONFIG_DIR', 'OPENCODE_DATA_DIR', 'AMP_DATA_DIR', 'DROID_SESSIONS_DIR',
    'CODEBUFF_DATA_DIR', 'HERMES_HOME', 'PI_AGENT_DIR', 'GOOSE_PATH_ROOT', 'KILO_DATA_DIR',
    'KIMI_DATA_DIR', 'OPENCLAW_DIR', 'QWEN_DATA_DIR', 'COPILOT_HOME', 'COPILOT_OTEL_FILE_EXPORTER_PATH',
    'ANTIGRAVITY_DATA_DIR', 'GROK_HOME', 'ZCODE_HOME', 'GEMINI_DATA_DIR'];
  for (const name of sources) environment[name] = config.sourceDirectories?.[name] ?? path.join(config.dataDirectory, 'disabled-sources', name);
  fs.mkdirSync(path.join(environment.CLAUDE_CONFIG_DIR, 'projects'), { recursive: true });
  environment.CODEX_HOME = config.codexHome;
  return environment;
}

function execute(config, args) {
  const command = config.pnpmPath;
  const environment = childEnvironment(config);
  const options = { env: environment, cwd: config.dataDirectory, encoding: 'utf8', windowsHide: true,
    timeout: 20 * 60 * 1000, maxBuffer: 128 * 1024 * 1024 };
  let result;
  if (process.platform === 'win32' && /\.(cmd|bat)$/i.test(command)) {
    if (/["%\r\n]/.test(command) || args.some(arg => /[^\w@./:=,-]/.test(arg))) throw new Error('Unsafe pnpm arguments.');
    result = spawnSync(process.env.ComSpec ?? 'cmd.exe', ['/d', '/s', '/c', `""${command}" ${args.join(' ')}"`], { ...options, windowsVerbatimArguments: true });
  } else result = spawnSync(command, args, options);
  if (result.error || result.status !== 0) throw new Error(`Collector failed (${args.join(' ')}): ${result.error?.message ?? result.stderr?.trim().slice(-1200) ?? result.status}`);
  const raw = result.stdout.replace(/^\uFEFF/, '').trim();
  const report = JSON.parse(raw);
  if (!report || typeof report !== 'object') throw new Error('Collector did not produce a JSON object.');
  return report;
}

export function mergeCcusage(config, dailyReport, sessionReport, detailedDailyReport, observedAt) {
  const data = config.dataDirectory;
  const all = mergeReport(path.join(data, 'usage-all.aggregate.json'), reportRows(dailyReport, 'daily'), 'daily', observedAt);
  const session = mergeReport(path.join(data, 'usage-session.aggregate.json'), reportRows(sessionReport, 'session'), 'session', observedAt);
  const perAgent = reportRows(detailedDailyReport ?? dailyReport, 'daily').flatMap(row => {
    if (Array.isArray(row.agents)) return row.agents.map(agent => ({ ...agent, period: row.period ?? row.date }));
    if (row.agent && row.agent !== 'all') return [row];
    if (row.metadata?.agents?.length === 1) return [{ ...row, agent: row.metadata.agents[0] }];
    return []; // Never attribute a mixed day to Codex.
  });
  const agents = mergeReport(path.join(data, 'usage-agents.aggregate.json'), perAgent, 'daily', observedAt);
  const names = loadTitles(config.codexHome, session.session.filter(row => row.agent === 'codex').map(sessionId));
  // Keep known titles if the local index is subsequently pruned.
  for (const row of session.session) if (row.agent === 'codex') row.displayName = names.get(sessionId(row)) ?? row.displayName ?? `Session ${sessionId(row).slice(0, 8)}`;
  atomicJson(path.join(data, 'usage-session.aggregate.json'), session);
  atomicJson(path.join(data, 'codex-hover.json'), makeHover(agents.daily, session.session, observedAt, names));
  return { dailyRecords: all.daily.length, sessionRecords: session.session.length, agentDailyRecords: agents.daily.length };
}

async function withLock(directory, action) {
  fs.mkdirSync(directory, { recursive: true });
  const filename = path.join(directory, 'collector.lock');
  try {
    const fd = fs.openSync(filename, 'wx');
    fs.writeFileSync(fd, JSON.stringify({ pid: process.pid, startedAt: new Date().toISOString() }));
    fs.closeSync(fd);
  } catch (error) {
    if (error.code !== 'EEXIST') throw error;
    const existing = readJson(filename, {});
    let running = true;
    try { process.kill(existing.pid, 0); } catch { running = false; }
    if (running) return { skipped: 'Another collector is running.' };
    fs.unlinkSync(filename);
    return withLock(directory, action);
  }
  try { return await action(); } finally { fs.unlinkSync(filename); }
}

export async function collectCursor(config, observedAt = new Date().toISOString()) {
  if (!config.cursorAgent?.enabled) return { provider: 'cursor-agent', status: 'disabled' };
  const settings = config.cursorAgent;
  if (!fs.existsSync(path.join(settings.dataDirectory, 'projects'))) return { provider: 'cursor-agent', status: 'no local transcripts' };
  // sqlite fallback copies stay on D:. The provider never loads CLI config or calls loadPricing().
  process.env.CODEBURN_CACHE_DIR = path.join(config.dataDirectory, 'codeburn-cache');
  process.env.CODEBURN_PRICING_SNAPSHOT_ONLY = '1';
  const { createCursorAgentProvider } = await import(pathToFileURL(path.join(settings.moduleDirectory, 'providers', 'cursor-agent.js')).href);
  const provider = createCursorAgentProvider(settings.dataDirectory);
  const sources = await provider.discoverSessions();
  const seenKeys = new Set();
  const rows = [];
  for (const source of sources) {
    for await (const call of provider.createSessionParser(source, seenKeys).parse()) {
      if (!call.deduplicationKey || !call.sessionId || !Number.isFinite(call.costUSD) || call.costUSD < 0) throw new Error('Invalid CodeBurn provider record.');
      rows.push({ id: call.deduplicationKey, sessionId: call.sessionId, timestamp: call.timestamp, agent: 'cursor-agent',
        model: call.model, inputTokens: call.inputTokens, outputTokens: call.outputTokens, reasoningTokens: call.reasoningTokens,
        totalTokens: call.inputTokens + call.outputTokens + call.reasoningTokens, totalCost: call.costUSD,
        estimated: true, estimationMethod: 'CodeBurn transcript characters / 4; model fallback may apply', observedAt });
    }
  }
  const filename = path.join(config.dataDirectory, 'usage-cursor-agent.aggregate.json');
  const previous = readJson(filename, { schemaVersion: 1, calls: [] });
  if (previous.schemaVersion !== 1 || !Array.isArray(previous.calls)) throw new Error('Unsupported Cursor aggregate.');
  const merged = new Map(previous.calls.map(row => [row.id, row]));
  // A partially written last turn can grow: replace its stable turn key instead of adding it twice.
  for (const row of rows) merged.set(row.id, { ...row, firstSeenAt: merged.get(row.id)?.firstSeenAt ?? observedAt });
  const calls = [...merged.values()].sort((a, b) => a.id.localeCompare(b.id));
  const sessions = new Map();
  for (const row of calls) {
    const session = sessions.get(row.sessionId) ?? { agent: 'cursor-agent', sessionId: row.sessionId, totalCost: 0, totalTokens: 0, lastActivity: row.timestamp, estimated: true };
    session.totalCost += row.totalCost;
    session.totalTokens += row.totalTokens;
    if (Date.parse(row.timestamp) > Date.parse(session.lastActivity)) session.lastActivity = row.timestamp;
    sessions.set(row.sessionId, session);
  }
  const result = { schemaVersion: 1, provider: 'cursor-agent', generatedAt: observedAt, collector: 'codeburn@0.9.25 passive Cursor Agent provider',
    estimated: true, costBasis: 'Approximate API-equivalent USD; token counts inferred from transcript characters, not recorded usage or billed spending',
    calls, sessions: [...sessions.values()], totals: totals(calls) };
  atomicJson(path.join(config.dataDirectory, 'usage-cursor-agent.latest.json'), { schemaVersion: 1, generatedAt: observedAt, estimated: true, calls: rows });
  atomicJson(filename, result);
  return { provider: 'cursor-agent', status: rows.length ? 'estimated data collected' : 'no recognized local transcripts', discoveredFiles: sources.length, incomingCalls: rows.length, retainedCalls: calls.length, sessions: sessions.size };
}

export async function collectAntigravityNative(config, observedAt = new Date().toISOString()) {
  const settings = config.antigravityNative;
  if (!settings?.enabled) return { status: 'disabled' };
  process.env.CODEBURN_CACHE_DIR = path.join(config.dataDirectory, 'codeburn-cache');
  process.env.CODEBURN_PRICING_SNAPSHOT_ONLY = '1';
  const { createAntigravityProvider, discoverAntigravitySessionSources, flushAntigravityCache } =
    await import(pathToFileURL(path.join(settings.moduleDirectory, 'providers', 'antigravity.js')).href);
  const sources = await discoverAntigravitySessionSources(settings.conversationDirectories.map(dir => ({ dir, project: 'antigravity', extensions: ['.pb'] })));
  const provider = createAntigravityProvider();
  const seenKeys = new Set();
  const incoming = [];
  try {
    for (const source of sources) {
      for await (const call of provider.createSessionParser(source, seenKeys).parse()) {
        if (!call.deduplicationKey || !call.sessionId || !Number.isFinite(call.costUSD) || call.costUSD < 0) throw new Error('Invalid Antigravity record.');
        incoming.push({ id: call.deduplicationKey, sessionId: call.sessionId, timestamp: call.timestamp,
          agent: 'antigravity', model: call.model, inputTokens: call.inputTokens, outputTokens: call.outputTokens,
          reasoningTokens: call.reasoningTokens, totalTokens: call.inputTokens + call.outputTokens + call.reasoningTokens,
          totalCost: call.costUSD, observedAt });
      }
    }
  } finally { await flushAntigravityCache(); }
  const filename = path.join(config.dataDirectory, 'usage-antigravity-native.aggregate.json');
  const previous = readJson(filename, { schemaVersion: 1, calls: [] });
  if (previous.schemaVersion !== 1 || !Array.isArray(previous.calls)) throw new Error('Unsupported Antigravity aggregate.');
  const rows = new Map(previous.calls.map(row => [row.id, row]));
  for (const row of incoming) rows.set(row.id, { ...row, firstSeenAt: rows.get(row.id)?.firstSeenAt ?? observedAt });
  const calls = [...rows.values()].sort((a, b) => a.id.localeCompare(b.id));
  atomicJson(filename, { schemaVersion: 1, provider: 'antigravity', generatedAt: observedAt,
    collector: 'codeburn@0.9.25 native Antigravity provider', costBasis: 'API-equivalent USD estimates from local language-server token records; cache buckets absent and missing output fields may undercount',
    mergePolicy: 'latest snapshot per stable response ID; missing records retained; separate from ccusage to avoid overlap', calls, totals: totals(calls) });
  return { status: incoming.length ? 'native language-server records or cache collected' : 'no native token records available; open Antigravity before an eligible run',
    discoveredFiles: sources.length, incomingCalls: incoming.length, retainedCalls: calls.length };
}

export async function run(config, { mergeOnly = false, now = new Date().toISOString() } = {}) {
  return withLock(config.dataDirectory, async () => {
    const stateFile = path.join(config.dataDirectory, 'collector-state.json');
    const state = readJson(stateFile, {});
    if (!mergeOnly && !weeklyDue(state, Date.parse(now))) return { skipped: 'Last collection attempt was less than seven days ago.', nextEligibleAt: new Date(Date.parse(state.lastAttemptAt) + WEEK_MS).toISOString() };
    if (!mergeOnly) atomicJson(stateFile, { ...state, lastAttemptAt: now, lastStatus: 'running' });
    try {
      let dailyReport, sessionReport, detailedDailyReport;
      if (mergeOnly) {
        dailyReport = readJson(config.dailyReportPath);
        sessionReport = readJson(config.sessionReportPath);
      } else {
        const packageName = `ccusage@${config.ccusageVersion}`;
        dailyReport = execute(config, ['dlx', packageName, '--json']);
        reportRows(dailyReport, 'daily');
        atomicJson(path.join(config.dataDirectory, 'usage-all.latest.json'), dailyReport);
        sessionReport = execute(config, ['dlx', packageName, 'session', '--json']);
        reportRows(sessionReport, 'session');
        atomicJson(path.join(config.dataDirectory, 'usage-session.latest.json'), sessionReport);
        // Agent-separated daily data is needed for Codex-only period costs.
        detailedDailyReport = execute(config, ['dlx', packageName, 'daily', '--by-agent', '--json']);
        reportRows(detailedDailyReport, 'daily');
        atomicJson(config.dailyReportPath, dailyReport);
        atomicJson(config.sessionReportPath, sessionReport);
        atomicJson(path.join(config.dataDirectory, 'usage-by-agent.latest.json'), detailedDailyReport);
      }
      const summary = mergeCcusage(config, dailyReport, sessionReport, detailedDailyReport, now);
      if (!mergeOnly) {
        const sources = Object.fromEntries(['codex', 'copilot', 'antigravity'].map(agent => [agent,
          { sessions: reportRows(sessionReport, 'session').filter(row => row.agent === agent).length,
            status: reportRows(sessionReport, 'session').some(row => row.agent === agent) ? 'collected' : 'no local token records found' }]));
        try { sources['cursor-agent'] = await collectCursor(config, now); }
        catch (error) { sources['cursor-agent'] = { status: 'failed', error: error.message }; }
        if (sources.antigravity.sessions === 0) {
          try { sources.antigravity.native = await collectAntigravityNative(config, now); }
          catch (error) { sources.antigravity.native = { status: 'failed', error: error.message }; }
        }
        summary.sources = sources;
        atomicJson(path.join(config.dataDirectory, 'source-status.json'), { generatedAt: now, sources });
        atomicJson(stateFile, { lastAttemptAt: now, lastSuccessAt: now, lastStatus: sources['cursor-agent'].status === 'failed' || sources.antigravity.native?.status === 'failed' ? 'partial' : 'success', ...summary });
      }
      return { ...summary, mergeOnly, generatedAt: now };
    } catch (error) {
      if (!mergeOnly) atomicJson(stateFile, { ...state, lastAttemptAt: now, lastStatus: 'failed', error: error.message });
      throw error;
    }
  });
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    const args = process.argv.slice(2);
    const configIndex = args.indexOf('--config');
    if (configIndex < 0 || !args[configIndex + 1]) throw new Error('Usage: node usage-collector.mjs --config collector-config.json [--merge-only]');
    const config = readJson(path.resolve(args[configIndex + 1]));
    const result = await run(config, { mergeOnly: args.includes('--merge-only') });
    process.stdout.write(JSON.stringify(result, null, 2) + '\n');
  } catch (error) {
    process.stderr.write(`${error.message}\n`);
    process.exitCode = 1;
  }
}
