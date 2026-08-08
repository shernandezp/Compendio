import '@testing-library/jest-dom/vitest';

import { initI18n } from '../i18n';

/**
 * Real translations in component tests, not raw keys.
 *
 * Without this every rendered label is the key itself, so a test can only assert on roles and test
 * ids — and the assertions that matter here are about wording that changes with state ("Improve
 * writing" versus "Improve selection"). Asserting on keys would pass while the user-facing sentence
 * said the wrong thing.
 */
await initI18n('en');

/**
 * jsdom does not implement matchMedia, and Mantine's responsive hooks ask for it on mount. Without
 * this every component test fails on a browser API rather than on anything about the component.
 */
Object.defineProperty(window, 'matchMedia', {
  writable: true,
  value: (query: string) => ({
    matches: false,
    media: query,
    onchange: null,
    addListener: () => {},
    removeListener: () => {},
    addEventListener: () => {},
    removeEventListener: () => {},
    dispatchEvent: () => false,
  }),
});

window.scrollTo = () => {};

/**
 * Nor ResizeObserver, which Mantine's ScrollArea constructs on mount. Same reasoning as above: a
 * component that happens to scroll should not be untestable because of it.
 */
globalThis.ResizeObserver ??= class {
  observe() {}
  unobserve() {}
  disconnect() {}
};

/**
 * Nor geometry on `Range`, which ProseMirror's cursor plugin measures on every selection change.
 *
 * Zeroes are the honest answer: jsdom does no layout, so any number here would be invented. Nothing
 * under test asserts on cursor position — the editor tests are about which *text* is selected and
 * what comes back as Markdown, which is geometry-independent. Without the stub the plugin throws
 * from a timer, outside any test's stack, and the editor is simply untestable.
 */
const zeroRect = () => ({
  x: 0, y: 0, width: 0, height: 0, top: 0, right: 0, bottom: 0, left: 0, toJSON: () => ({}),
}) as DOMRect;

Range.prototype.getClientRects ??= () =>
  Object.assign([], { item: () => null }) as unknown as DOMRectList;

Range.prototype.getBoundingClientRect ??= zeroRect;

/**
 * Nor the CSS Font Loading API, which Mantine's autosizing Textarea listens to so it can re-measure
 * once a webfont swaps in. Same reasoning again: the AI proposal dialog is a textarea, and it should
 * not be untestable because jsdom ships no font loader.
 */
Object.defineProperty(document, 'fonts', {
  writable: true,
  value: {
    addEventListener: () => {},
    removeEventListener: () => {},
    ready: Promise.resolve(),
  },
});
