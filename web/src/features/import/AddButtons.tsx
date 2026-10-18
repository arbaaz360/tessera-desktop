import { useRef } from 'react';
import { isTauri } from '@/shared/api/core';
import { pickFiles, pickFolder } from '@/shared/api/shell';
import { useImports } from './ImportContext';
import { MEDIA_ACCEPT, type ImportTarget } from './api';

/**
 * "Add pictures" and "Add folder", for the grid and an album.
 *
 * Add pictures opens the system file picker (several at once, pictures and
 * videos only). What is picked from inside a library folder is indexed where
 * it is; anything else is copied into the Tessera folder, under the album's
 * name. Add folder indexes the folder where it is, never copying it, and
 * puts its pictures in the album when there is one.
 *
 * Outside Tauri there is no system picker: a file input stands in (the
 * bytes go up instead of paths) and a prompt takes a folder's path.
 */
export function AddButtons({ target, variant = 'toolbar' }: { target: ImportTarget | null; variant?: 'toolbar' | 'empty' }) {
  const { importPaths, importFiles } = useImports();
  const input = useRef<HTMLInputElement>(null);

  const addPictures = async () => {
    if (!isTauri()) {
      input.current?.click();
      return;
    }
    const paths = await pickFiles();
    if (paths) await importPaths(paths, target);
  };

  const addFolder = async () => {
    const folder = isTauri()
      ? await pickFolder()
      : window.prompt('The full path of a folder to add, as it is on this computer')?.trim() || null;
    if (folder) await importPaths([folder], target);
  };

  const primary =
    variant === 'empty'
      ? 'rounded-md bg-accent px-4 py-2 text-sm font-semibold text-ink-inverse transition-colors hover:bg-accent-hover'
      : 'inline-flex items-center gap-1.5 rounded-md bg-accent px-3 py-1.5 text-sm font-medium text-ink-inverse transition-colors hover:bg-accent-hover';
  const secondary =
    variant === 'empty'
      ? 'rounded-md border border-line px-4 py-2 text-sm font-medium text-ink-muted transition-colors hover:bg-raised hover:text-ink'
      : 'inline-flex items-center gap-1.5 rounded-md border border-line px-3 py-1.5 text-sm text-ink-muted transition-colors hover:bg-raised hover:text-ink';

  return (
    <>
      <button
        type="button"
        onClick={() => void addPictures()}
        className={primary}
        title={target ? `Add pictures or videos to “${target.albumName}”` : 'Add pictures or videos to your library'}
      >
        {variant === 'toolbar' && <PlusIcon />}
        Add pictures
      </button>
      <button
        type="button"
        onClick={() => void addFolder()}
        className={secondary}
        title="Add a folder: indexed where it is, never copied"
      >
        {variant === 'toolbar' && <FolderIcon />}
        Add folder
      </button>
      <input
        ref={input}
        type="file"
        multiple
        accept={MEDIA_ACCEPT}
        hidden
        data-testid="add-pictures-input"
        onChange={(event) => {
          const files = [...(event.currentTarget.files ?? [])];
          event.currentTarget.value = '';
          if (files.length) void importFiles(files, target);
        }}
      />
    </>
  );
}

/**
 * What an empty grid says: how to fill it, with the two buttons. The words
 * name the drop because a picture dragged from a folder is the quickest way
 * in, and nothing else on screen would say it works.
 */
export function EmptyLibraryHint({ target, lead }: { target: ImportTarget | null; lead: string }) {
  return (
    <div className="max-w-md">
      <p className="text-ink">{lead}</p>
      <p className="mt-1 text-sm text-ink-faint">
        Drag pictures, videos or a folder onto this window, or choose them here. Pictures already in a
        library folder stay where they are; anything else is copied into the Tessera folder.
      </p>
      <div className="mt-4 flex flex-wrap justify-center gap-2">
        <AddButtons target={target} variant="empty" />
      </div>
    </div>
  );
}

function PlusIcon() {
  return (
    <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" aria-hidden>
      <path d="M12 5v14M5 12h14" />
    </svg>
  );
}

function FolderIcon() {
  return (
    <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" aria-hidden>
      <path d="M3 7h6l2 2h10v10a2 2 0 0 1-2 2H3z" />
    </svg>
  );
}
