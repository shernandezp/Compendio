#!/usr/bin/env node
/**
 * Key parity between locales, plus every `t('…')` in the source resolving in all of them.
 *
 * A CI gate rather than a lint suggestion. i18next's runtime fallback to English stays enabled as a
 * safety net, but a missing key is a bug — the failure mode it hides is a Spanish instance quietly
 * answering in English on the one screen nobody tested.
 */
import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join, dirname, relative } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const localesDir = join(here, '..', 'src', 'i18n', 'locales');
const sourceDir = join(here, '..', 'src');

const REFERENCE = 'en';

function flatten(object, prefix = '') {
  const keys = new Set();
  for (const [key, value] of Object.entries(object)) {
    const path = prefix ? `${prefix}.${key}` : key;
    if (value && typeof value === 'object' && !Array.isArray(value)) {
      for (const nested of flatten(value, path)) keys.add(nested);
    } else {
      keys.add(path);
    }
  }
  return keys;
}

function walk(directory) {
  const files = [];
  for (const entry of readdirSync(directory)) {
    const full = join(directory, entry);
    if (statSync(full).isDirectory()) {
      if (entry !== 'node_modules' && entry !== 'locales') files.push(...walk(full));
    } else if (/\.(ts|tsx)$/.test(entry)) {
      files.push(full);
    }
  }
  return files;
}

const catalogs = new Map();
for (const file of readdirSync(localesDir).filter((f) => f.endsWith('.json'))) {
  const language = file.replace('.json', '');
  catalogs.set(language, flatten(JSON.parse(readFileSync(join(localesDir, file), 'utf8'))));
}

const reference = catalogs.get(REFERENCE);
if (!reference) {
  console.error(`check:i18n — no ${REFERENCE}.json to compare against.`);
  process.exit(1);
}

const errors = [];
const warnings = [];

// 1. Key-set parity. A missing key fails; an extra one warns, because it is usually a leftover.
for (const [language, keys] of catalogs) {
  if (language === REFERENCE) continue;

  for (const key of reference) {
    // i18next plural suffixes are generated per language, so `_one`/`_other` need not match 1:1.
    if (!keys.has(key) && !hasPluralSibling(keys, key)) {
      errors.push(`${language}.json is missing "${key}"`);
    }
  }

  for (const key of keys) {
    if (!reference.has(key) && !hasPluralSibling(reference, key)) {
      warnings.push(`${language}.json has "${key}", which ${REFERENCE}.json does not`);
    }
  }
}

// 2. Every literal t('…') in the source resolves in every catalog.
const literal = /\bt\(\s*'([a-zA-Z0-9_.]+)'/g;
for (const file of walk(sourceDir)) {
  const source = readFileSync(file, 'utf8');
  for (const match of source.matchAll(literal)) {
    const key = match[1];
    for (const [language, keys] of catalogs) {
      if (!keys.has(key) && !hasPluralSibling(keys, key)) {
        errors.push(`${relative(sourceDir, file)} uses t('${key}'), missing from ${language}.json`);
      }
    }
  }
}

function hasPluralSibling(keys, key) {
  return keys.has(`${key}_one`) || keys.has(`${key}_other`) || /_(one|other|many|few|zero|two)$/.test(key);
}

for (const warning of new Set(warnings)) console.warn(`warning: ${warning}`);

if (errors.length > 0) {
  for (const error of new Set(errors)) console.error(`error: ${error}`);
  console.error(`\ncheck:i18n failed with ${new Set(errors).size} problem(s).`);
  process.exit(1);
}

console.log(`check:i18n passed — ${reference.size} keys across ${catalogs.size} locales.`);
