/**
 * The Content-Security-Policy nonce for this page load.
 *
 * The server generates one per response and substitutes it into the shell; everything that injects
 * a `<style>` element at runtime has to carry it, because the policy is
 * `style-src-elem 'self' 'nonce-…'` with no `'unsafe-inline'`. That is Mantine, which creates a
 * style element for its CSS variables, and Mermaid, whose rendered SVG contains one.
 *
 * In the Vite dev server there is no substitution and no CSP header, so the placeholder is still
 * there — treated as "no nonce" rather than passed on as a literal attribute value.
 */
const PLACEHOLDER = '__CSP_NONCE__';

function read(): string | undefined {
  const meta = document.querySelector<HTMLMetaElement>('meta[name="csp-nonce"]');
  const value = meta?.content?.trim();

  return !value || value === PLACEHOLDER ? undefined : value;
}

export const cspNonce = read();

/**
 * Stamps the nonce onto the `<style>` elements inside a markup string.
 *
 * For markup that is about to be assigned with `innerHTML`: a style element is checked against the
 * policy when it is inserted, so the attribute has to already be in the string. Setting `.nonce`
 * afterwards is too late.
 */
export function withStyleNonce(markup: string): string {
  if (!cspNonce) {
    return markup;
  }

  return markup.replace(/<style(?=[\s>])/gi, `<style nonce="${cspNonce}"`);
}
