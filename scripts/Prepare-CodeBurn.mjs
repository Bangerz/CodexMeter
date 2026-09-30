import fs from 'node:fs';
import path from 'node:path';
import { stripTypeScriptTypes } from 'node:module';
import { createHash } from 'node:crypto';

// CodeBurn publishes a bundled CLI and its original sources in the source map.
// Extract only the passive Cursor Agent provider graph, avoiding CLI config reads.
const [packageDirectory, outputDirectory] = process.argv.slice(2);
if (!packageDirectory || !outputDirectory) throw new Error('Usage: node Prepare-CodeBurn.mjs <verified package directory> <module output directory>');
const metadata = JSON.parse(fs.readFileSync(path.join(packageDirectory, 'package.json'), 'utf8'));
if (metadata.name !== 'codeburn' || metadata.version !== '0.9.25') throw new Error('Expected pinned CodeBurn 0.9.25.');
const map = JSON.parse(fs.readFileSync(path.join(packageDirectory, 'dist', 'main.js.map'), 'utf8'));
const modules = ['providers/cursor-agent.ts', 'models.ts', 'sqlite.ts', 'content-utils.ts', 'token-estimate.ts',
  'cache-dir.ts', 'fetch-utils.ts', 'providers/antigravity.ts', 'bash-utils.ts',
  'data/litellm-snapshot.json', 'data/pricing-fallback.json'];
const manifest = [];
for (const name of modules) {
  const index = map.sources.findIndex(source => source.endsWith(`/src/${name}`));
  if (index < 0 || typeof map.sourcesContent[index] !== 'string') throw new Error(`Missing published source: ${name}`);
  const source = map.sourcesContent[index];
  const filename = path.join(outputDirectory, name.replace(/\.ts$/, '.js'));
  fs.mkdirSync(path.dirname(filename), { recursive: true });
  fs.writeFileSync(filename, name.endsWith('.ts') ? stripTypeScriptTypes(source, { mode: 'strip' }) : source, 'utf8');
  manifest.push({ source: map.sources[index], sha256: createHash('sha256').update(source).digest('hex') });
}
fs.writeFileSync(path.join(outputDirectory, 'package.json'), JSON.stringify({ type: 'module', private: true, codeburnVersion: metadata.version }));
fs.writeFileSync(path.join(outputDirectory, 'provenance.json'), JSON.stringify({ package: 'codeburn', version: metadata.version,
  origin: 'https://registry.npmjs.org/codeburn/-/codeburn-0.9.25.tgz', transformation: 'Node stripTypeScriptTypes; no source logic changes', manifest }, null, 2));
process.stdout.write(`Prepared ${modules.length} pinned passive provider modules.\n`);
