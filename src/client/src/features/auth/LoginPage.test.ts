import { describe, expect, it } from 'vitest';

import { safeNext } from './LoginPage';

describe('safeNext', () => {
  it('keeps a path on this origin', () => {
    expect(safeNext('/p/IT/vpn.md?x=1#top')).toBe('/p/IT/vpn.md?x=1#top');
  });

  it.each([null, '', 'https://evil.example', '//evil.example', '/\\evil.example', 'p/IT'])(
    'falls back to the root for %s',
    (next) => {
      expect(safeNext(next)).toBe('/');
    },
  );
});
