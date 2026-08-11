import { describe, expect, it } from 'vitest';

import { attachmentUrl, matchAttachment } from './attachmentRefs';

/**
 * Tying a picture on screen back to the file behind it, which is what decides whether the enlarged
 * view offers to delete it. Both ways of being wrong are bad: failing to recognize this page's own
 * image hides the button, and recognizing one that is not ours offers a delete that cannot work.
 */
describe('matchAttachment', () => {
  const attachments = [{ path: 'Runbooks/assets/rack.png' }, { path: 'Routers/Router #2/assets/front.png' }];

  it('finds the attachment behind an absolute image src', () => {
    const src = `${window.location.origin}/api/v1/attachments/Runbooks/assets/rack.png`;

    expect(matchAttachment(src, attachments)).toBe(attachments[0]);
  });

  /** The browser hands back an encoded `src`; the attachment list holds the plain path. */
  it('finds one whose path needed encoding', () => {
    const src = `${window.location.origin}${attachmentUrl('Routers/Router #2/assets/front.png')}`;

    expect(matchAttachment(src, attachments)).toBe(attachments[1]);
  });

  it('returns nothing for an image this page does not own', () => {
    expect(matchAttachment('https://example.com/logo.png', attachments)).toBeUndefined();
    expect(matchAttachment('/api/v1/attachments/Other/assets/rack.png', attachments)).toBeUndefined();
  });
});
