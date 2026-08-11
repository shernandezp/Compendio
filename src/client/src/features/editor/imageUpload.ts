import type { Node as ProseNode, Schema } from '@milkdown/kit/prose/model';
import { notifications } from '@mantine/notifications';

import { api, ApiError, encodePath } from '../../lib/api';

/** Just the lookup, so this module needs nothing else from i18next. */
type Translate = (key: string) => string;

/**
 * Turning dropped, pasted and picked image files into attachments the page can point at.
 *
 * @remarks
 * Extracted from the editor rather than written inline, because the failures here are the silent
 * kind and a test cannot reach inside a mount effect. Two of them write to the page: an image node
 * with an empty <c>src</c> when the server refused the file, and a <c>data:</c> URL — Milkdown's
 * own default — which puts a megabyte of one screenshot inside the <c>.md</c> file, unreadable in
 * VS Code and copied again into every version snapshot.
 */
export function createImageUploader({ pagePath, t }: { pagePath: string; t: Translate }) {
  /**
   * Uploads one image and returns the URL to point at, or <c>null</c> having said why.
   *
   * The server already answers every rejection in the reader's own language — too large, type not
   * on the allowlist, too many on this page, no write access — and until this existed nobody ever
   * saw any of it: Milkdown's upload path ends in <c>console.error</c>. Somebody whose 30 MB photo
   * silently fails to appear concludes the feature is broken.
   */
  async function uploadImage(file: File): Promise<string | null> {
    if (!pagePath) {
      // A page that does not exist yet has nowhere to put an attachment — the API keys them by page
      // path. Saying so beats an image that quietly never arrives.
      notifications.show({ color: 'yellow', message: t('editor.image.needsSave') });
      return null;
    }

    try {
      const uploaded = await api.uploadAttachment(pagePath, file);
      return `/api/v1/attachments/${encodePath(uploaded.path)}`;
    } catch (error) {
      notifications.show({
        color: 'red',
        message: error instanceof ApiError && error.detail ? error.detail : t('editor.image.failed'),
      });

      return null;
    }
  }

  /**
   * The uploader Milkdown's paste and drop handling calls, replacing the base64 one.
   *
   * Images only. The gesture is consumed by the time this runs — the browser will not open the
   * dropped file itself — so a file this cannot use has to be said out loud rather than dropped on
   * the floor.
   */
  async function uploader(files: FileList, schema: Schema): Promise<ProseNode[]> {
    const images = Array.from(files).filter((file) => file.type.startsWith('image/'));

    if (images.length < files.length) {
      notifications.show({ color: 'yellow', message: t('editor.image.onlyImages') });
    }

    const nodes: ProseNode[] = [];

    for (const file of images) {
      const url = await uploadImage(file);

      if (!url) {
        continue;
      }

      // The inline image node, not Crepe's image block: it fits wherever the cursor happens to be,
      // and its alt text is real alt text. The block form spends that slot on a display ratio.
      const node = schema.nodes.image?.createAndFill({ src: url, alt: file.name });

      if (node) {
        nodes.push(node);
      }
    }

    return nodes;
  }

  return { uploadImage, uploader };
}
