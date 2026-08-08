/**
 * Mermaid, loaded on demand and locked down.
 *
 * `securityLevel: 'strict'` and never `'loose'`. Diagram source is user-authored page content, and
 * Mermaid has had a CSS-injection advisory — it is untrusted input like everything else on a page.
 *
 * The CSP is `style-src 'self' 'nonce-…'`, so the style element Mermaid injects carries the
 * response nonce rather than the policy being widened to `'unsafe-inline'`.
 */
import { withStyleNonce } from './csp';

let initialized = false;

async function ensureInitialized() {
  const mermaid = (await import('mermaid')).default;

  if (!initialized) {
    mermaid.initialize({
      startOnLoad: false,
      securityLevel: 'strict',
      theme: document.documentElement.dataset.mantineColorScheme === 'dark' ? 'dark' : 'default',
      fontFamily: 'system-ui, -apple-system, "Segoe UI", Roboto, sans-serif',
    });

    initialized = true;
  }

  return mermaid;
}

/** Renders every `<pre class="mermaid">` inside a container. */
export async function renderDiagrams(container: HTMLElement): Promise<void> {
  const blocks = Array.from(container.querySelectorAll<HTMLElement>('pre.mermaid:not([data-processed])'));
  if (blocks.length === 0) {
    return;
  }

  const mermaid = await ensureInitialized();

  for (const [index, block] of blocks.entries()) {
    const source = block.textContent ?? '';

    try {
      const { svg } = await mermaid.render(`compendio-diagram-${index}-${Date.now()}`, source);

      // The rendered SVG carries its own <style> element. It is checked against the policy when it
      // is inserted, so the nonce has to be in the markup before the assignment — setting it on the
      // element afterwards is too late and the diagram renders unstyled.
      block.innerHTML = withStyleNonce(svg);
      block.dataset.processed = 'true';
    } catch {
      // A diagram that does not parse stays as its source text. Showing the source is more useful
      // than an error box, and it is what the author needs in order to fix it.
      block.dataset.processed = 'failed';
    }
  }
}
