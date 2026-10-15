-- Folders are albums, and Tessera watches for them.
--
-- Immich had four "folder-album creator" containers: every five minutes,
-- each top-level folder under an account's library became an album of that
-- name. They died on 2026-08-23 (a DTO change) and nobody noticed for 18
-- days; Immich itself retired on 2026-09-12. The rule moves here, where the
-- albums live, and runs at the end of every scan that could have changed it.
--
-- album_folder is where an account's album folders sit, relative to the
-- root: '' means the root's own children, 'PICS/immich-albums' means that
-- subtree's children, NULL means folders under this root are not albums
-- (the simpcity corpus). It reproduces exactly what the four creators did.

ALTER TABLE scan_root ADD COLUMN album_folder text;

UPDATE scan_root SET album_folder = ''
 WHERE abs_path IN ('/reddit',
                    '/library/bf5e8711-4e2b-45f3-84a4-20738f0be4b2',
                    '/library/274e2f91-7d8d-4478-b03b-00288bc25c42',
                    '/library/96e7f049-ce60-47a5-9548-a6ebefd14d85');
UPDATE scan_root SET album_folder = 'PICS/immich-albums' WHERE abs_path = '/mnt/e';

-- What the watch knows about each folder it minds: the direct children of
-- the album folder (or of the root when there is none). One row per folder,
-- plus one row with folder = '' for the parent itself, whose mtime moves
-- when a child folder appears or goes.
--
-- Directory mtimes over the bind mount cost ~5 ms each and there are 10,000
-- children under one root, so the watch never stats them all at once: the
-- parent is one stat per tick, children are swept a few hundred per tick.
CREATE TABLE scan_folder (
    root_id    bigint      NOT NULL REFERENCES scan_root ON DELETE CASCADE,
    folder     text        NOT NULL,            -- relative to the root; '' is the parent
    mtime      timestamptz,                     -- as last observed; NULL until swept
    entries    integer,                         -- names in it at the last look
    first_seen timestamptz NOT NULL DEFAULT now(),
    checked_at timestamptz,                     -- last stat, for the sweep's round-robin
    changed_at timestamptz,                     -- a change noticed and not yet scanned; NULL = in step
    scanned_at timestamptz,
    PRIMARY KEY (root_id, folder)
);

CREATE INDEX scan_folder_sweep_idx   ON scan_folder (root_id, checked_at NULLS FIRST) WHERE changed_at IS NULL;
CREATE INDEX scan_folder_changed_idx ON scan_folder (changed_at) WHERE changed_at IS NOT NULL;
