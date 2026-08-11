import { useRef } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';

import { ImageLightbox } from './ImageLightbox';

/**
 * The images in a page are not React's — they arrive as rendered HTML — so the thing worth testing
 * is the binding: that every image the reader can click becomes clickable, that the one image that
 * must not be hijacked is left alone, and that a keyboard reaches the same place a mouse does.
 */
describe('ImageLightbox', () => {
  afterEach(cleanup);

  type HostProps = Pick<Parameters<typeof ImageLightbox>[0], 'attachments' | 'onDelete'> & { html: string };

  function Host({ html, attachments, onDelete }: HostProps) {
    const container = useRef<HTMLDivElement>(null);

    return (
      <MantineProvider>
        <div ref={container} className="compendio-content" dangerouslySetInnerHTML={{ __html: html }} />
        <ImageLightbox containerRef={container} html={html} attachments={attachments} onDelete={onDelete} />
      </MantineProvider>
    );
  }

  const renderWith = (html: string, props: Omit<HostProps, 'html'> = {}) => {
    const view = render(<Host html={html} {...props} />);
    return { ...view, image: () => view.container.querySelector('img')! };
  };

  it('opens the original image in a dialog when the preview is clicked', async () => {
    const { image } = renderWith('<p><img src="/api/v1/attachments/Runbooks/assets/rack.png" alt="The rack"></p>');

    expect(screen.queryByRole('dialog')).toBeNull();

    fireEvent.click(image());

    const dialog = await screen.findByRole('dialog');
    const shown = dialog.querySelector('img.compendio-lightbox-image');

    // The same file, not a thumbnail of it: the dialog exists to show what the preview shrank.
    // Resolved against the document, which is what the browser fetched.
    expect(shown?.getAttribute('src')).toContain('/api/v1/attachments/Runbooks/assets/rack.png');
    expect(dialog).toHaveTextContent('The rack');
  });

  it('opens from the keyboard, so the preview is not a mouse-only affordance', async () => {
    const { image } = renderWith('<p><img src="/api/v1/attachments/a/assets/one.png" alt=""></p>');

    expect(image()).toHaveAttribute('role', 'button');
    expect(image().tabIndex).toBe(0);

    fireEvent.keyDown(image(), { key: 'Enter' });

    expect(await screen.findByRole('dialog')).toBeInTheDocument();
  });

  /**
   * The editor's image block writes `![1.00](url "Caption")` — the caption in `title`, the block's
   * display ratio in the alt slot. Naming the dialog after the alt would call every captioned
   * image "1.00", and have a screen reader announce it as one.
   */
  it('names the image after its caption, never after the editor ratio in its alt', async () => {
    const { image } = renderWith(
      '<p><img src="/api/v1/attachments/a/assets/rack.png" alt="1.00" title="Rack 3, rear"></p>',
    );

    expect(image().getAttribute('aria-label')).toContain('Rack 3, rear');

    fireEvent.click(image());

    const dialog = await screen.findByRole('dialog');

    expect(dialog).toHaveTextContent('Rack 3, rear');
    expect(dialog).not.toHaveTextContent('1.00');
  });

  it('falls back to a plain name when the alt is a ratio and there is no caption', async () => {
    const { image } = renderWith('<p><img src="/api/v1/attachments/a/assets/rack.png" alt="1.78"></p>');

    expect(image().getAttribute('aria-label')).not.toContain('1.78');

    fireEvent.click(image());

    expect(await screen.findByRole('dialog')).toHaveTextContent('Image');
  });

  /**
   * Deleting is offered from the dialog, and only where it means something: the picture has to be a
   * file this page owns, and the reader has to be able to write. An offer that 403s, or that points
   * at somebody else's server, is worse than no offer.
   */
  describe('deleting', () => {
    const rack = { path: 'Runbooks/assets/rack.png', name: 'rack.png' };
    const embedded = '<p><img src="/api/v1/attachments/Runbooks/assets/rack.png" alt="The rack"></p>';

    it('hands the attachment to the caller and closes, leaving the confirmation to them', async () => {
      const onDelete = vi.fn();
      const { image } = renderWith(embedded, { attachments: [rack], onDelete });

      fireEvent.click(image());
      fireEvent.click(await screen.findByRole('button', { name: 'Delete the file' }));

      expect(onDelete).toHaveBeenCalledWith(rack);

      // The page underneath is about to lose this picture; a dialog still showing it is the wrong
      // thing to be looking at.
      await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    });

    it('offers nothing to a reader who cannot write', async () => {
      const { image } = renderWith(embedded, { attachments: [rack] });

      fireEvent.click(image());
      await screen.findByRole('dialog');

      expect(screen.queryByRole('button', { name: 'Delete the file' })).toBeNull();
    });

    it('offers nothing for an image this page does not own', async () => {
      const { image } = renderWith('<p><img src="https://example.com/logo.png" alt="Logo"></p>', {
        attachments: [rack],
        onDelete: vi.fn(),
      });

      fireEvent.click(image());
      await screen.findByRole('dialog');

      expect(screen.queryByRole('button', { name: 'Delete the file' })).toBeNull();
    });
  });

  /**
   * `[![alt](image)](/p/Somewhere)` is a picture the author put a destination behind. Zooming it
   * would swallow the click and strand the reader on the page they were trying to leave.
   */
  it('leaves a linked image to its link', () => {
    const { image } = renderWith('<p><a href="/p/Runbooks"><img src="/api/v1/attachments/a/assets/x.png" alt="x"></a></p>');

    expect(image()).not.toHaveClass('compendio-zoomable');

    fireEvent.click(image());

    expect(screen.queryByRole('dialog')).toBeNull();
  });
});
