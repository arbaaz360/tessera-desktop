-- Albums the folder rule made, told apart from albums a person made; which
-- roots' folders made them; the Tessera folder; and imports.

-- ---------------------------------------------------------------- origin

-- 'folder': the folder rule made it (FolderRepository.ReconcileAlbumsAsync).
-- 'manual': a person did, or took it over by naming or renaming it.
-- Only a folder album is ever deleted by the app, and only when none of the
-- roots that made it is left and it holds no live picture.
ALTER TABLE album ADD COLUMN origin TEXT NOT NULL DEFAULT 'manual'
    CONSTRAINT album_origin_known CHECK (origin IN ('manual', 'folder'));

-- The roots whose folders imply an album. Several roots can each have a
-- folder of the same name; the album is one row and each root a link.
-- Removing a root drops its links (cascade) and then the leftovers go.
CREATE TABLE album_root (
    album_id INTEGER NOT NULL REFERENCES album ON DELETE CASCADE,
    root_id  INTEGER NOT NULL REFERENCES scan_root ON DELETE CASCADE,
    PRIMARY KEY (album_id, root_id)
);
CREATE INDEX album_root_root_idx ON album_root (root_id);

-- ---------------------------------------------------------------- Tessera folder

-- 1 for a folder the app made for itself (Pictures/Tessera by default): the
-- one place it writes, copying in what was dropped from outside every root.
-- The current one is the setting 'import.folder'; an earlier one keeps the
-- mark so it may stay nested inside a person's root without being scanned
-- twice.
ALTER TABLE scan_root ADD COLUMN app_owned INTEGER NOT NULL DEFAULT 0;

-- ---------------------------------------------------------------- imports

-- One row per drop, file pick or folder add through POST /api/import, with
-- its counts. The per-file answers live in memory while the core runs.
CREATE TABLE import_run (
    import_id   INTEGER PRIMARY KEY AUTOINCREMENT,
    album_id    INTEGER REFERENCES album ON DELETE SET NULL,
    started_at  INTEGER NOT NULL DEFAULT (CAST((julianday('now') - 2440587.5) * 86400000 AS INTEGER)),
    finished_at INTEGER,
    total       INTEGER NOT NULL DEFAULT 0,
    done        INTEGER NOT NULL DEFAULT 0,
    added       INTEGER NOT NULL DEFAULT 0,   -- new pictures indexed where they are
    already     INTEGER NOT NULL DEFAULT 0,   -- the same bytes were in the library
    copied      INTEGER NOT NULL DEFAULT 0,   -- copied into the Tessera folder
    skipped     INTEGER NOT NULL DEFAULT 0,   -- not a picture, unreadable, failed
    folders     INTEGER NOT NULL DEFAULT 0,   -- folders indexed in place
    cancelled   INTEGER NOT NULL DEFAULT 0
);

-- ---------------------------------------------------------------- the albums already here

-- Before this migration nothing recorded which albums the folder rule made,
-- so it is read from how they were made. The rule makes a scan's albums in
-- ONE statement, at the end of the scan that first saw their pictures, and
-- fills them in the next: an album born with its members, within moments of
-- those pictures being first seen. A person cannot do that - they see a
-- picture, then select it, then name an album - and no person makes two
-- albums in the same millisecond, which every scan with two folders does.
--
-- Counted as the folder's, all of:
--   nobody has edited it: no description, no chosen cover, not renamed or
--   emptied since its last member arrived (both move updated_at);
--   its first members arrived within 2 s of the album row;
--   and either the album row came within 2 s of those members' first
--   sighting, or another album was made in the very same millisecond.
-- Anything else stays a person's album. Missing a folder album only leaves a
-- leftover where it was; mistaking a person's album would delete it.
WITH first AS (
    SELECT album_id, min(added_at) AS first_added, max(added_at) AS last_added
      FROM album_item
     GROUP BY album_id
),
born AS (
    SELECT a.album_id, a.created_at,
           (SELECT max(c.first_seen)
              FROM album_item i JOIN content c ON c.content_id = i.content_id
             WHERE i.album_id = a.album_id AND i.added_at = f.first_added) AS newest_seen
      FROM album a
      JOIN first f ON f.album_id = a.album_id
     WHERE a.description = '' AND a.cover_content_id IS NULL
       AND a.updated_at <= f.last_added
       AND f.first_added - a.created_at BETWEEN 0 AND 2000
)
UPDATE album SET origin = 'folder'
 WHERE album_id IN (
       SELECT b.album_id FROM born b
        WHERE b.created_at - b.newest_seen BETWEEN 0 AND 2000
           OR EXISTS (SELECT 1 FROM album o
                       WHERE o.album_id <> b.album_id AND o.created_at = b.created_at));

-- Link each folder album to the roots that still hold a folder of its name
-- with one of its pictures in it.
INSERT OR IGNORE INTO album_root (album_id, root_id)
SELECT DISTINCT a.album_id, r.root_id
  FROM album a
  JOIN album_item i ON i.album_id = a.album_id
  JOIN location l   ON l.content_id = i.content_id
  JOIN scan_root r  ON r.root_id = l.root_id AND r.album_folder IS NOT NULL
  JOIN (SELECT root_id, CASE WHEN album_folder = '' THEN '' ELSE album_folder || '/' END AS prefix
          FROM scan_root) p ON p.root_id = r.root_id
 WHERE a.origin = 'folder'
   AND substr(l.rel_path, 1, length(p.prefix)) = p.prefix
   AND instr(substr(l.rel_path, length(p.prefix) + 1), '/') > 0
   AND substr(substr(l.rel_path, length(p.prefix) + 1), 1,
              instr(substr(l.rel_path, length(p.prefix) + 1), '/') - 1) = a.name;

-- The leftovers: folder albums no root holds any more, with no live picture
-- (a picture is live when it has a location neither trashed nor gone, which
-- is what timeline_entry holds). The same rule removing a root applies.
DELETE FROM album
 WHERE origin = 'folder'
   AND NOT EXISTS (SELECT 1 FROM album_root ar WHERE ar.album_id = album.album_id)
   AND NOT EXISTS (SELECT 1 FROM album_item i
                     JOIN timeline_entry t ON t.account_id = album.account_id AND t.content_id = i.content_id
                    WHERE i.album_id = album.album_id);
