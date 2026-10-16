import { afterEach, describe, expect, it, vi } from 'vitest';
import { useEffect } from 'react';
import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { mockCore, renderPage } from '@/test/render';
import type { AlbumDto } from '@/shared/api/types';
import type { TimelineAsset } from '@/shared/api/timeline';
import { SelectionProvider, useSelection } from './SelectionContext';
import { SelectionBar, addedMessage } from './SelectionBar';

/**
 * "Add to album" closed the picker and kept the selection, and said nothing:
 * in the Windows UI test (2026-10-16) there was no way to tell the add had
 * happened without opening the album.
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

const lakes: AlbumDto = {
  id: '4',
  albumName: 'Lakes and dunes',
  description: '',
  ownerId: '1',
  albumThumbnailAssetId: null,
  createdAt: '2026-10-16T08:26:00.853Z',
  updatedAt: '2026-10-16T08:26:00.853Z',
  assetCount: 0,
  shared: false,
  hasSharedLink: false,
  isActivityEnabled: false,
  albumUsers: [],
  assets: [],
  order: 'desc',
};

/** Selects two pictures on mount, as two clicks on the grid would. */
function Seed() {
  const { toggle } = useSelection();
  useEffect(() => {
    toggle(asset('a'), '2024-04-01');
    toggle(asset('b'), '2024-04-01');
  }, [toggle]);
  return null;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('SelectionBar', () => {
  it('says what "Add to album" did', async () => {
    const user = userEvent.setup();
    const { calls } = mockCore([
      { method: 'GET', path: '/api/albums', answer: { json: [lakes] } },
      {
        method: 'PUT',
        path: '/api/albums/4/assets',
        answer: { json: [{ id: 'a', success: true, error: null }, { id: 'b', success: false, error: 'duplicate' }] },
      },
    ]);
    renderPage(
      <SelectionProvider>
        <Seed />
        <SelectionBar />
      </SelectionProvider>,
    );

    expect(await screen.findByText('2 selected')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Add to album' }));
    await user.click(await screen.findByRole('button', { name: /Lakes and dunes/ }));

    expect(await screen.findByRole('status')).toHaveTextContent('Added 1 to Lakes and dunes · 1 already there');
    expect(calls.find((call) => call.method === 'PUT')?.body).toEqual({ ids: ['a', 'b'] });
    // The selection stays for the next action.
    expect(screen.getByText('2 selected')).toBeInTheDocument();
  });
});

describe('addedMessage', () => {
  it('counts what the core added', () => {
    expect(addedMessage([{ id: 'a', success: true, error: null }], 1, 'Lakes')).toBe('Added 1 to Lakes');
    expect(addedMessage(undefined, 3, 'Lakes')).toBe('Added 3 to Lakes');
  });
});
