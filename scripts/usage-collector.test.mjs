import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { mergeRows, totals, weeklyDue, WEEK_MS, makeHover, readJson, collectCursor, collectAntigravityNative, atomicJson } from './usage-collector.mjs';

const scratch = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', 'artifacts', 'collector-tests');
fs.mkdirSync(scratch, { recursive: true });

test('Windows PowerShell UTF-16 report imports correctly', () => {
  const filename = path.join(scratch, 'utf16.json');
  fs.writeFileSync(filename, Buffer.from('\uFEFF{"daily":[],"totals":{}}', 'utf16le'));
  assert.deepEqual(readJson(filename), { daily: [], totals: {} });
});

test('atomic reports can be replaced without losing valid JSON', () => {
  const filename = path.join(scratch, 'atomic.json');
  atomicJson(filename, { generation: 1 });
  atomicJson(filename, { generation: 2 });
  assert.equal(readJson(filename).generation, 2);
});

test('repeated snapshots replace totals; missing sessions retained; Codex UUID survives path changes', () => {
  const id = '01a0ebc0-4ebc-7480-aa53-c2bc99ad4e7a';
  const initial = [{ agent: 'codex', period: `sessions/rollout-${id}`, totalCost: 4, totalTokens: 100, displayName: 'Retained name' },
    { agent: 'copilot', period: 'other', totalCost: 2 }];
  const first = mergeRows([], initial, 'session', '2026-09-20T00:00:00Z');
  const next = mergeRows(first, [{ ...initial[0], period: `archive/${id}`, totalCost: 5 }], 'session', '2026-09-29T00:00:00Z');
  assert.equal(next.length, 2);
  const withoutNames = mergeRows(next, [{ agent: 'codex', period: id, totalCost: 5 }], 'session', '2026-09-30T00:00:00Z');
  assert.equal(withoutNames.find(row => row.agent === 'codex').displayName, 'Retained name');
  assert.equal(totals(next).totalCost, 7);
  assert.equal(totals(mergeRows(next, initial, 'session', '2026-09-19T00:00:00Z')).totalCost, 7);
  assert.equal(totals(mergeRows(next, [{ ...initial[0], totalCost: 5 }], 'session', '2026-09-29T00:00:00Z')).totalCost, 7);
});

test('Cursor provider turn snapshots deduplicate and grow without adding repeated exports', async () => {
  const dataDirectory = path.join(scratch, 'cursor-fixture');
  const root = path.join(dataDirectory, 'cursor');
  const moduleDirectory = path.join(dataDirectory, 'fake-provider');
  fs.mkdirSync(path.join(root, 'projects'), { recursive: true });
  fs.mkdirSync(path.join(moduleDirectory, 'providers'), { recursive: true });
  atomicJson(path.join(moduleDirectory, 'package.json'), { type: 'module' });
  fs.writeFileSync(path.join(moduleDirectory, 'providers', 'cursor-agent.js'), `export function createCursorAgentProvider(){return {discoverSessions:async()=>[{}],createSessionParser:()=>({async *parse(){yield {deduplicationKey:'cursor-agent:session:0',sessionId:'session',timestamp:'2026-09-29T00:00:00Z',costUSD:Number(process.env.CURSOR_FIXTURE_COST),inputTokens:10,outputTokens:20,reasoningTokens:2,model:'estimated'};}})}};`);
  const config = { dataDirectory, cursorAgent: { enabled: true, dataDirectory: root, moduleDirectory } };
  process.env.CURSOR_FIXTURE_COST = '1';
  await collectCursor(config);
  await collectCursor(config);
  assert.equal(readJson(path.join(dataDirectory, 'usage-cursor-agent.aggregate.json')).totals.totalCost, 1);
  process.env.CURSOR_FIXTURE_COST = '2';
  await collectCursor(config);
  const result = readJson(path.join(dataDirectory, 'usage-cursor-agent.aggregate.json'));
  assert.equal(result.calls.length, 1);
  assert.equal(result.totals.totalCost, 2);
  assert.equal(result.estimated, true);
  delete process.env.CURSOR_FIXTURE_COST;
});

test('Antigravity response IDs deduplicate and cache flushes without mixing ccusage totals', async () => {
  const dataDirectory = path.join(scratch, 'antigravity-fixture');
  const moduleDirectory = path.join(dataDirectory, 'fake-provider');
  fs.mkdirSync(path.join(moduleDirectory, 'providers'), { recursive: true });
  atomicJson(path.join(moduleDirectory, 'package.json'), { type: 'module' });
  fs.writeFileSync(path.join(moduleDirectory, 'providers', 'antigravity.js'), `export async function discoverAntigravitySessionSources(roots){if(roots[0].dir!=='approved')throw Error('root mismatch');return [{}]};export async function flushAntigravityCache(){process.env.ANTIGRAVITY_FIXTURE_FLUSHED='yes'};export function createAntigravityProvider(){return{createSessionParser:()=>({async *parse(){yield{deduplicationKey:'antigravity:response',sessionId:'session',timestamp:'2026-09-29T00:00:00Z',costUSD:2,inputTokens:10,outputTokens:20,reasoningTokens:2,model:'recorded'}}})}};`);
  const config = { dataDirectory, antigravityNative: { enabled: true, conversationDirectories: ['approved'], moduleDirectory } };
  await collectAntigravityNative(config);
  await collectAntigravityNative(config);
  const result = readJson(path.join(dataDirectory, 'usage-antigravity-native.aggregate.json'));
  assert.equal(result.calls.length, 1);
  assert.equal(result.totals.totalCost, 2);
  assert.equal(process.env.ANTIGRAVITY_FIXTURE_FLUSHED, 'yes');
  assert.equal(fs.existsSync(path.join(dataDirectory, 'usage-all.aggregate.json')), false);
  delete process.env.ANTIGRAVITY_FIXTURE_FLUSHED;
});

test('daily identity includes agent and replacing a changing day does not add costs', () => {
  const first = mergeRows([], [{ agent: 'all', period: '2026-09-29', totalCost: 10 }], 'daily', '2026-09-29T00:00:00Z');
  const next = mergeRows(first, [{ agent: 'all', period: '2026-09-29', totalCost: 12 }, { agent: 'all', period: '2026-09-30', totalCost: 2 }], 'daily', '2026-09-30T00:00:00Z');
  assert.equal(next.length, 2);
  assert.equal(totals(next).totalCost, 14);
  assert.throws(() => mergeRows([], [{ period: 'bad', totalCost: 2 }], 'daily', '2026-09-30T00:00:00Z'));
  assert.throws(() => mergeRows([], [{ period: '2026-09-29', totalCost: -2 }], 'daily', '2026-09-30T00:00:00Z'));
});

test('weekly guard uses attempts and remains closed through clock rollback', () => {
  const instant = Date.parse('2026-09-29T00:00:00Z');
  const state = { lastAttemptAt: new Date(instant).toISOString(), lastStatus: 'failed' };
  assert.equal(weeklyDue({}, instant), true);
  assert.equal(weeklyDue(state, instant + WEEK_MS - 1), false);
  assert.equal(weeklyDue(state, instant + WEEK_MS), true);
  assert.equal(weeklyDue(state, instant - 1), false);
});

test('hover is Codex-only and orders five sessions by activity with display names', () => {
  const sessions = Array.from({ length: 6 }, (_, i) => ({ agent: 'codex', id: `id${i}`, totalCost: i, metadata: { lastActivity: `2026-09-${String(20+i).padStart(2, '0')}T00:00:00Z` } }));
  sessions.push({ agent: 'copilot', id: 'other', totalCost: 1000, lastActivity: '2026-09-29T00:00:00Z' });
  const hover = makeHover([{ agent: 'codex', period: '2026-09-29', totalCost: 5 }, { agent: 'copilot', period: '2026-09-29', totalCost: 1000 }], sessions, '2026-09-29T00:00:00Z', new Map([['id5', 'Named conversation']]));
  assert.equal(hover.totalCost, 5);
  assert.equal(hover.recentSessions.length, 5);
  assert.equal(hover.recentSessions[0].displayName, 'Named conversation');
  assert.equal(hover.recentSessions[0].id, 'id5');
});
