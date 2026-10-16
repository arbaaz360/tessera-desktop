import { afterEach, describe, expect, it, vi } from 'vitest';
import { useState } from 'react';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient } from '@tanstack/react-query';
import { mockCore, renderPage } from '@/test/render';
import { qk } from '@/shared/api/queries';
import type { TimelineAsset } from '@/shared/api/timeline';
import { invalidateAfterTrashChange } from '@/features/selection/api';
import { AssetViewer } from './AssetViewer';

/**
 * What the Windows UI test (2026-10-16) found in the viewer: a picture
 * trashed from it stayed in its list (the counter still said 24 and one step
 * back showed it again), and opened from the Trash page it offered "Move to
 * trash" and no way to restore.
 */

function asset(id: string): TimelineAsset {
  return {
    id,
    ratio: 1.6,
    thumbhash: null,
    isImage: true,
    isFavorite: false,
    durationMs: null,
    fileCreatedAt: '2024-04-01T12:52:00.000Z',
    livePhotoVideoId: null,
  };
}

const ASSETS = [asset('a'), asset('b'), asset('c')];

function Harness({ mode = 'library' }: { mode?: 'library' | 'trash' }) {
  const [index, setIndex] = useState(0);
  const [open, setOpen] = useState(true);
  if (!open) return <p>viewer closed</p>;
  return (
    <AssetViewer assets={ASSETS} index={index} onIndexChange={setIndex} onClose={() => setOpen(false)} mode={mode} />
  );
}

/** The picture on show: the original's URL names it. */
function shownId(): string | undefined {
  const img = document.querySelector<HTMLImageElement>('img[src*="/original"]');
  return img?.src.match(/assets\/([^/]+)\/original/)?.[1];
}

function counter(): string | undefined {
  return screen.getByRole('dialog', { name: 'Picture viewer' }).textContent?.match(/\d+ \/ \d+/)?.[0];
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('AssetViewer', () => {
  it('drops a trashed picture from its count and from stepping', async () => {
    const user = userEvent.setup();
    const { calls } = mockCore([{ method: 'DELETE', path: '/api/assets', answer: { status: 204 } }]);
    renderPage(<Harness />);

    expect(await screen.findByText('1 / 3')).toBeInTheDocument();
    expect(shownId()).toBe('a');

    await user.click(screen.getByRole('button', { name: 'Move to trash' }));
    const confirm = screen.getByRole('dialog', { name: 'Move to trash?' });
    await user.click(within(confirm).getByRole('button', { name: 'Move to trash' }));

    await waitFor(() => expect(counter()).toBe('1 / 2'));
    expect(shownId()).toBe('b');
    expect(calls.find((call) => call.method === 'DELETE')?.body).toEqual({ ids: ['a'], force: false });

    // One step back must not bring the trashed picture back.
    await user.keyboard('{ArrowLeft}');
    expect(shownId()).toBe('b');
    expect(screen.getByRole('button', { name: 'Previous' })).toBeDisabled();

    await user.keyboard('{ArrowRight}');
    expect(counter()).toBe('2 / 2');
    expect(shownId()).toBe('c');
  });

  it('in the trash: offers Restore, not Trash or Favourite, and moves on after restoring', async () => {
    const user = userEvent.setup();
    const { calls } = mockCore([
      { method: 'POST', path: '/api/trash/restore/assets', answer: { json: { count: 1 } } },
    ]);
    renderPage(<Harness mode="trash" />);

    expect(await screen.findByText('1 / 3')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Move to trash' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Add to favourites' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Find pictures like this one' })).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Restore' }));

    await waitFor(() => expect(counter()).toBe('1 / 2'));
    expect(shownId()).toBe('b');
    expect(calls.find((call) => call.path === '/api/trash/restore/assets')?.body).toEqual({ ids: ['a'] });

    // Restoring the rest closes the viewer: nothing is left to show.
    await user.click(screen.getByRole('button', { name: 'Restore' }));
    await waitFor(() => expect(shownId()).toBe('c'));
    await user.click(screen.getByRole('button', { name: 'Restore' }));
    expect(await screen.findByText('viewer closed')).toBeInTheDocument();
  });
});

describe('invalidateAfterTrashChange', () => {
  it('refreshes the library counts as well as the timelines', async () => {
    const client = new QueryClient();
    const spy = vi.spyOn(client, 'invalidateQueries');
    await invalidateAfterTrashChange(client);
    expect(spy).toHaveBeenCalledWith({ queryKey: qk.timeline.all });
    expect(spy).toHaveBeenCalledWith({ queryKey: qk.library.all });
  });
});
