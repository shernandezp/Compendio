import { encodePath } from '../../lib/api';

/**
 * Tying an image in a rendered page back to the file behind it.
 *
 * Only the matching lives here. Removing an image from the Markdown when its file is deleted is the
 * server's job — see `MarkdownImages` — so that both halves happen in one request, against one
 * hash, and cannot half-succeed.
 */

/** The URL a page points at to show an attachment. */
export const attachmentUrl = (path: string) => `/api/v1/attachments/${encodePath(path)}`;

/**
 * Compares URLs by what they mean rather than by how they are spelled.
 *
 * The editor writes percent-encoded URLs; a page written in VS Code has the plain characters. Both
 * point at the same file, and a delete that only recognized one of them would leave the other
 * behind as a broken image.
 */
function sameTarget(a: string, b: string): boolean {
  return decode(a) === decode(b);
}

function decode(url: string): string {
  const raw = url.trim().replace(/^<|>$/g, '');

  try {
    return decodeURIComponent(raw);
  } catch {
    // A stray '%' that is not an escape. The raw text is then its own best comparison.
    return raw;
  }
}

/**
 * Finds the attachment an image in the page is showing, if it is one of this page's.
 *
 * @param src The resolved `img.src`, which the browser has already made absolute and encoded.
 */
export function matchAttachment<T extends { path: string }>(src: string, attachments: readonly T[]): T | undefined {
  let pathname: string;

  try {
    pathname = new URL(src, window.location.origin).pathname;
  } catch {
    return undefined;
  }

  return attachments.find((attachment) => sameTarget(pathname, attachmentUrl(attachment.path)));
}
