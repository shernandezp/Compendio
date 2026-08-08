#!/usr/bin/env node
/**
 * Normalizes an OpenAPI document into the committed form.
 *
 * The contract is committed and CI fails on a diff, which only works if "the same API" always
 * serializes to the same bytes. So: keys sorted, two-space indent, a trailing newline, and no
 * escaping of non-ASCII — a Spanish summary must not turn into `ó` in one place and stay
 * `ó` in another.
 *
 * Usage: node scripts/format-openapi.mjs <input.json> <output.json>
 */
import { readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { dirname } from 'node:path';

const [input, output] = process.argv.slice(2);

if (!input || !output) {
  console.error('Usage: node scripts/format-openapi.mjs <input.json> <output.json>');
  process.exit(1);
}

/** Recursively sorts object keys. Arrays keep their order — order is meaningful in OpenAPI. */
function sortKeys(value) {
  if (Array.isArray(value)) {
    return value.map(sortKeys);
  }

  if (value !== null && typeof value === 'object') {
    return Object.fromEntries(
      Object.keys(value)
        .sort()
        .map((key) => [key, sortKeys(value[key])]),
    );
  }

  return value;
}

const document = JSON.parse(readFileSync(input, 'utf8'));

/**
 * `servers` is dropped, not sorted.
 *
 * The framework fills it in with whatever address the generating process happened to bind, so the
 * committed document would record `http://127.0.0.1:8093/` and CI, which binds 8099, would fail on
 * a diff that says nothing about the API. It is not part of the contract either: this document
 * describes an instance the reader is already talking to, wherever that is.
 */
delete document.servers;

mkdirSync(dirname(output), { recursive: true });
writeFileSync(output, `${JSON.stringify(sortKeys(document), null, 2)}\n`, 'utf8');

console.log(`Wrote ${output}`);
