import { readdirSync, readFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { describe, expect, it } from 'vitest';
import { createInitialSnapshot, foldAssistantEvents } from './assistant-ui.contract';

/**
 * These specs guard the *vendoring*, not the fold semantics — those belong to
 * US-404's codec fixtures, and duplicating them here would create a second place
 * to update whenever the package changes.
 *
 * The byte-for-byte comparison against the NuGet copy lives in
 * `scripts/check-andes-contract.mjs`, because it needs a restored .NET package
 * and `npm test` has to stay runnable without one.
 */

const SRC_ROOT = resolve(import.meta.dirname, '../../../..');
const VENDORED = join(SRC_ROOT, 'app/domain/stream/andes/assistant-ui.contract.ts');
const SPEC = join(SRC_ROOT, 'app/domain/stream/andes/assistant-ui.contract.spec.ts');

const VENDORED_SYMBOLS = [
  'AssistantUiEvent',
  'AssistantActivity',
  'AssistantStatusSnapshot',
  'foldAssistantEvents',
] as const;

function typeScriptFiles(dir: string): string[] {
  return readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    const path = join(dir, entry.name);
    if (entry.isDirectory()) return typeScriptFiles(path);
    return entry.name.endsWith('.ts') ? [path] : [];
  });
}

describe('vendored Andes contract', () => {
  it('is the only declaration of its public symbols under src/', () => {
    const files = typeScriptFiles(SRC_ROOT).filter((file) => file !== VENDORED && file !== SPEC);

    const duplicates = files.flatMap((file) => {
      const source = readFileSync(file, 'utf8');
      return VENDORED_SYMBOLS.filter((symbol) =>
        new RegExp(
          `^\\s*(export\\s+)?(declare\\s+)?(interface|type|class|enum|function|const|let|var)\\s+${symbol}\\b`,
          'm',
        ).test(source),
      ).map((symbol) => `${symbol} in ${file}`);
    });

    expect(duplicates).toEqual([]);
  });

  it('keeps the header markers the drift check splits on', () => {
    const text = readFileSync(VENDORED, 'utf8');

    expect(text).toContain('// --- END VENDOR HEADER');
    expect(text).toMatch(/@andes-contract-package\s+andes\.extensions\.ai\.ui/);
    expect(text).toMatch(/@andes-contract-version\s+0\.7\.0/);
    expect(text).toMatch(/@andes-contract-source\s+typescript\/andes-assistant-ui\.ts/);
  });

  it('exports a loadable module, not merely a parseable one', () => {
    expect(createInitialSnapshot()).toEqual({ phase: 'Running', activities: [] });
    expect(typeof foldAssistantEvents).toBe('function');
  });
});
