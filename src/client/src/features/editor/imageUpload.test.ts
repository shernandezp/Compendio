import { afterEach, describe, expect, it, vi } from 'vitest';
import type { Schema } from '@milkdown/kit/prose/model';

import { createImageUploader } from './imageUpload';

/**
 * What a pasted screenshot turns into.
 *
 * @remarks
 * Two failures are worth a test here, neither of them throws, and both end up written into the
 * file. A refused upload — too large, wrong type, read-only folder — must insert nothing: Crepe's
 * own uploader passes whatever comes back straight into the image node, so an empty `src` is what
 * a rejection would leave behind, saved and permanent. And the URL must be an attachment and never
 * a base64 `data:` one, which is Milkdown's default and puts a megabyte of screenshot inside the
 * `.md` file — invisible until somebody opens the folder in VS Code.
 */
describe('createImageUploader', () => {
  afterEach(() => vi.restoreAllMocks());

  const t = (key: string) => key;

  const png = (name = 'screenshot.png') => new File([new Uint8Array([0x89, 0x50, 0x4e, 0x47])], name, { type: 'image/png' });

  /** An array-like standing in for the `FileList` a paste or drop hands over. */
  const list = (...files: File[]) => files as unknown as FileList;

  /** Enough of a ProseMirror schema to record what the uploader tried to build. */
  const schema = () => {
    const built: Record<string, unknown>[] = [];

    return {
      built,
      schema: {
        nodes: { image: { createAndFill: (attrs: Record<string, unknown>) => (built.push(attrs), { attrs }) } },
      } as unknown as Schema,
    };
  };

  const answerWith = (status: number, body: unknown) =>
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(
      new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } }),
    );

  it('uploads the file and points the document at the attachment, never at a data: URL', async () => {
    const fetched = answerWith(200, { path: 'Runbooks/assets/screenshot.png', name: 'screenshot.png' });
    const { schema: model, built } = schema();

    const nodes = await createImageUploader({ pagePath: 'Runbooks/switches.md', t }).uploader(list(png()), model);

    expect(nodes).toHaveLength(1);
    expect(built[0]).toEqual({ src: '/api/v1/attachments/Runbooks/assets/screenshot.png', alt: 'screenshot.png' });
    expect(String(built[0]!.src)).not.toContain('data:');

    // Multipart, to the attachments endpoint, with the page it belongs to.
    const [url, init] = fetched.mock.calls[0]!;
    expect(String(url)).toBe('/api/v1/attachments');
    expect((init!.body as FormData).get('pagePath')).toBe('Runbooks/switches.md');
  });

  it('inserts nothing when the server refuses the file', async () => {
    answerWith(400, { code: 'attachment.too_large', title: 'Too large', detail: 'Attachments are limited to 25 MB.' });
    const { schema: model, built } = schema();

    const nodes = await createImageUploader({ pagePath: 'Runbooks/switches.md', t }).uploader(list(png()), model);

    // A broken image node would be worse than none: it saves into the file and stays there.
    expect(nodes).toEqual([]);
    expect(built).toEqual([]);
  });

  it('does not upload at all from a page that has not been saved yet', async () => {
    const fetched = answerWith(200, {});
    const { schema: model } = schema();

    const nodes = await createImageUploader({ pagePath: '', t }).uploader(list(png()), model);

    expect(nodes).toEqual([]);
    expect(fetched).not.toHaveBeenCalled();
  });

  it('ignores files that are not images', async () => {
    const fetched = answerWith(200, {});
    const { schema: model } = schema();
    const pdf = new File([new Uint8Array([1, 2, 3, 4])], 'report.pdf', { type: 'application/pdf' });

    const nodes = await createImageUploader({ pagePath: 'Runbooks/switches.md', t }).uploader(list(pdf), model);

    expect(nodes).toEqual([]);
    expect(fetched).not.toHaveBeenCalled();
  });

  it('percent-encodes the stored path, so a folder with a "#" in its name still resolves', async () => {
    answerWith(200, { path: 'Routers/Router #2/assets/rack.png', name: 'rack.png' });
    const { schema: model, built } = schema();

    await createImageUploader({ pagePath: 'Routers/Router #2/rack.md', t }).uploader(list(png('rack.png')), model);

    expect(built[0]!.src).toBe('/api/v1/attachments/Routers/Router%20%232/assets/rack.png');
  });
});
