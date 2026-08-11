import { useEffect, useState, type RefObject } from 'react';
import { useTranslation } from 'react-i18next';
import { Anchor, Button, Group, Modal, Stack, Text } from '@mantine/core';
import { IconTrash } from '@tabler/icons-react';

import { matchAttachment } from './attachmentRefs';

/** An attachment of the page being read, as much of one as this needs. */
export interface DeletableAttachment {
  path: string;
  name: string;
}

/** The image the reader asked to see, and what the modal needs in order to show it. */
interface ZoomedImage {
  src: string;
  /** What to call it, or empty when the image carries nothing worth reading out. */
  label: string;
}

/**
 * What an image in a page is actually called.
 *
 * @remarks
 * The caption comes first because the editor's image block puts it in `title` and spends the alt
 * slot on the block's display ratio: a captioned image serializes as `![1.00](url "Caption")`, so
 * an alt that is only a number is an editor artefact rather than a description of anything. Left
 * unchecked it would title this dialog "1.00" and have a screen reader announce it.
 */
function describe(image: HTMLImageElement): string {
  const caption = image.title.trim();

  if (caption) {
    return caption;
  }

  const alt = image.alt.trim();

  return /^\d+([.,]\d+)?$/.test(alt) ? '' : alt;
}

/**
 * Click an image in a rendered page and see it at full size.
 *
 * @remarks
 * <p>
 * The preview in the flow of the text is deliberately small: page content is capped at a readable
 * measure, so a screenshot of a switch configuration arrives scaled down to something nobody can
 * read. Without a way back to the original, the honest advice would be "download the attachment
 * and open it in a viewer", which is the workflow attachments exist to remove.
 * </p>
 * <p>
 * The images are not React's — the page body is rendered HTML assigned through
 * <c>dangerouslySetInnerHTML</c> — so this binds one listener on the container the same way the
 * checkbox handling in {@link PageView} does, rather than trying to own elements it did not create.
 * </p>
 */
export function ImageLightbox({
  containerRef,
  html,
  attachments = [],
  onDelete,
}: {
  containerRef: RefObject<HTMLElement | null>;
  /**
   * The markup currently in the container. Not read — it is what tells this to re-scan, because
   * new HTML means new `img` elements that have never been marked up as zoomable.
   */
  html?: string;
  /**
   * This page's attachments, so a picture can be traced back to the file behind it. An image the
   * page does not own — hotlinked from elsewhere — is not offered for deletion, because deleting it
   * is not something this page can do.
   */
  attachments?: readonly DeletableAttachment[];
  /** Absent for a reader without write access, which is what removes the button entirely. */
  onDelete?: (attachment: DeletableAttachment) => void;
}) {
  const { t } = useTranslation();
  const [zoomed, setZoomed] = useState<ZoomedImage | null>(null);
  const [natural, setNatural] = useState<{ width: number; height: number } | null>(null);

  const deletable = zoomed && onDelete ? matchAttachment(zoomed.src, attachments) : undefined;

  useEffect(() => {
    const container = containerRef.current;

    if (!container) {
      return;
    }

    // A linked image belongs to its link. Making it a zoom target would swallow the click and keep
    // the reader on this page, when the author put a destination behind the picture on purpose.
    const images = Array.from(container.querySelectorAll('img')).filter((image) => image.closest('a') === null);

    for (const image of images) {
      const label = describe(image);

      image.classList.add('compendio-zoomable');
      // Reachable by keyboard and announced as something that does something, because it now is.
      image.tabIndex = 0;
      image.setAttribute('role', 'button');
      image.setAttribute('aria-label', label ? t('page.imageZoomNamed', { name: label }) : t('page.imageZoom'));
    }

    const open = (image: HTMLImageElement) => {
      // `currentSrc` is what the browser actually fetched, so the modal shows the same file the
      // reader is looking at rather than re-resolving the attribute.
      setZoomed({ src: image.currentSrc || image.src, label: describe(image) });
      setNatural(null);
    };

    const onClick = (event: MouseEvent) => {
      const target = event.target;
      const image = target instanceof Element ? target.closest('img.compendio-zoomable') : null;

      if (image instanceof HTMLImageElement) {
        open(image);
      }
    };

    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key !== 'Enter' && event.key !== ' ') {
        return;
      }

      const image = event.target;

      if (!(image instanceof HTMLImageElement) || !image.classList.contains('compendio-zoomable')) {
        return;
      }

      // Space would otherwise scroll the page out from under the modal that is about to open.
      event.preventDefault();
      open(image);
    };

    container.addEventListener('click', onClick);
    container.addEventListener('keydown', onKeyDown);

    return () => {
      container.removeEventListener('click', onClick);
      container.removeEventListener('keydown', onKeyDown);
    };
  }, [containerRef, html, t]);

  return (
    <Modal
      opened={zoomed !== null}
      onClose={() => setZoomed(null)}
      // Sized by the image rather than by a breakpoint: a portrait phone photo and a wide network
      // diagram both want the whole viewport, and neither wants the same box.
      size="auto"
      centered
      padding="sm"
      title={zoomed?.label || t('page.image')}
      closeButtonProps={{ 'aria-label': t('common.close') }}
    >
      {zoomed && (
        <Stack gap="xs">
          <img
            key={zoomed.src}
            src={zoomed.src}
            alt={zoomed.label || t('page.image')}
            className="compendio-lightbox-image"
            onLoad={(event) =>
              setNatural({ width: event.currentTarget.naturalWidth, height: event.currentTarget.naturalHeight })
            }
          />

          <Group justify="space-between" gap="sm" wrap="nowrap">
            {/* The size is here because the modal is still a fit-to-viewport view of a file that may
                be larger than the screen. Saying so, and offering the file itself, is more use than
                a zoom control that fights the browser's own. */}
            <Text size="xs" c="dimmed">
              {natural ? t('page.imageDimensions', { width: natural.width, height: natural.height }) : ''}
            </Text>

            <Group gap="md" wrap="nowrap">
              <Anchor href={zoomed.src} target="_blank" rel="noreferrer" size="sm">
                {t('page.imageOriginal')}
              </Anchor>

              {/* Closed first: the page underneath is about to lose this picture, and a dialog left
                  showing a file that no longer exists is the wrong thing to look at. The
                  confirmation and every failure message belong to the caller's mutation. */}
              {deletable && (
                <Button
                  variant="subtle"
                  color="red"
                  size="compact-sm"
                  leftSection={<IconTrash size={14} />}
                  onClick={() => {
                    setZoomed(null);
                    onDelete?.(deletable);
                  }}
                >
                  {t('page.deleteAttachment')}
                </Button>
              )}
            </Group>
          </Group>
        </Stack>
      )}
    </Modal>
  );
}
