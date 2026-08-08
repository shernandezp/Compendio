/**
 * Local drafts.
 *
 * Never lose work is a promise, not an aspiration: a continuous local draft means a closed tab, a
 * crashed browser or a failed save costs nothing. `localStorage` rather than IndexedDB because a
 * page is a few kilobytes of text and a synchronous write on a debounce is simpler to reason about
 * than an async store that can be mid-transaction when the tab dies.
 */
const PREFIX = 'compendio.draft.';
const MAX_AGE_MS = 7 * 24 * 60 * 60 * 1000;

export interface Draft {
  content: string;
  baselineHash: string;
  savedAt: string;
}

export function saveDraft(path: string, content: string, baselineHash: string): void {
  try {
    const draft: Draft = { content, baselineHash, savedAt: new Date().toISOString() };
    localStorage.setItem(PREFIX + path, JSON.stringify(draft));
  } catch {
    // Quota or private mode. A draft is a safety net, not a dependency.
  }
}

export function loadDraft(path: string): Draft | null {
  try {
    const raw = localStorage.getItem(PREFIX + path);
    if (!raw) {
      return null;
    }

    const draft = JSON.parse(raw) as Draft;

    if (Date.now() - new Date(draft.savedAt).getTime() > MAX_AGE_MS) {
      clearDraft(path);
      return null;
    }

    return draft;
  } catch {
    return null;
  }
}

export function clearDraft(path: string): void {
  try {
    localStorage.removeItem(PREFIX + path);
  } catch {
    // Nothing to do, and nothing worth telling the user.
  }
}

/**
 * Follows a draft to the page's new path.
 *
 * A move does not touch the file's bytes, so the draft's baseline hash still matches and the editor
 * offers it exactly as it would have before. Without this the buffer stays in `localStorage` under
 * a path nothing will ever ask for again — which is losing work with extra steps, and renaming a
 * page from the tree while editing it is not an exotic thing to do.
 */
export function moveDraft(from: string, to: string): void {
  const draft = loadDraft(from);
  if (!draft) {
    return;
  }

  try {
    localStorage.setItem(PREFIX + to, JSON.stringify(draft));
    clearDraft(from);
  } catch {
    // The old copy stays put rather than being cleared into nothing.
  }
}

/** Housekeeping, so a long-lived browser profile does not accumulate abandoned drafts. */
export function pruneDrafts(): void {
  try {
    for (const key of Object.keys(localStorage)) {
      if (key.startsWith(PREFIX)) {
        loadDraft(key.slice(PREFIX.length));
      }
    }
  } catch {
    // Ignore.
  }
}
