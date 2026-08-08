import { describe, expect, it } from 'vitest';

import { withStyleNonce } from './csp';

/**
 * The nonce has to survive the whole chain, and every link in it is silent when it breaks.
 *
 * The server generates a nonce per response and puts it in the CSP header. If the shell has nowhere
 * to receive it, the header is still perfectly correct and the browser blocks the pre-mount theme
 * paint, Mantine's CSS variables and every Mermaid diagram — with no server-side error and nothing
 * in the UI to say why the page came up unstyled.
 */
describe('the CSP nonce', () => {
  // Read through Vite rather than the file system, so the test needs no Node type definitions.
  const shell = Object.values(
    import.meta.glob('../../index.html', { query: '?raw', import: 'default', eager: true }),
  )[0] as string;

  it('has a placeholder in the shell for the server to substitute', () => {
    // Twice: the meta tag the client reads, and the inline style block that paints the background
    // before React mounts.
    expect(shell.match(/__CSP_NONCE__/g)?.length).toBe(2);
    expect(shell).toContain('<meta name="csp-nonce" content="__CSP_NONCE__" />');
    expect(shell).toContain('<style nonce="__CSP_NONCE__">');
  });

  it('leaves markup alone when there is no nonce', () => {
    // The Vite dev server does no substitution, so the placeholder is treated as absent rather than
    // written out as a literal attribute value.
    expect(withStyleNonce('<svg><style>.a{}</style></svg>')).toBe('<svg><style>.a{}</style></svg>');
  });
});
