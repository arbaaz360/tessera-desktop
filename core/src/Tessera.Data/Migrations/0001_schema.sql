-- Tessera Desktop schema, SQLite.
--
-- ONE IDEA RUNS THROUGH ALL OF IT: a picture is identified by its bytes, never
-- by where it sits. Immich identified an external asset by path, so moving a
-- file minted a new asset and abandoned the old one - which on 2026-09-09/10
-- produced 122,000 broken embedding links, 150,511 stale face rows, 340,739
-- orphaned thumbnails, and a duplicate double-write that went unnoticed for ten
-- months. Here a move is one UPDATE and nothing downstream notices.
--
-- Ported from the HomeLab Postgres schema on 2026-10-16:
--   timestamptz -> INTEGER Unix milliseconds UTC      bytea  -> BLOB
--   citext      -> TEXT COLLATE NOCASE                text[] -> JSON text
--   identity    -> INTEGER PRIMARY KEY AUTOINCREMENT  now()  -> julianday arithmetic
-- Vectors do not live here: the index file under vectors/ holds them, and the
-- embedding table is the ledger of which content it holds.

-- ---------------------------------------------------------------- account

-- One built-in account, id 1. Every query is scoped to an account so a second
-- one is a row, not a rewrite (CONTRACT §2).
CREATE TABLE account (
    account_id   INTEGER PRIMARY KEY,
    display_name TEXT    NOT NULL,
    created_at   INTEGER NOT NULL DEFAULT (CAST((julianday('now') - 2440587.5) * 86400000 AS INTEGER)),
    CONSTRAINT account_name_not_blank CHECK (length(trim(display_name)) > 0)
);
INSERT INTO account (account_id, display_name) VALUES (1, 'Library');

-- ---------------------------------------------------------------- content

-- One row per distinct set of bytes, for ever. Nothing in this table changes
-- when a file moves, which is the entire point.
CREATE TABLE content (
    content_id  INTEGER PRIMARY KEY AUTOINCREMENT,
    sha256      BLOB    NOT NULL UNIQUE,
    byte_size   INTEGER NOT NULL,
    mime        TEXT    NOT NULL,
    width       INTEGER,
    height      INTEGER,
    duration_ms INTEGER,
    taken_at    INTEGER,
    taken_src   TEXT,                       -- 'exif' | 'filename' | 'mtime'
    first_seen  INTEGER NOT NULL DEFAULT (CAST((julianday('now') - 2440587.5) * 86400000 AS INTEGER)),
    CONSTRAINT content_sha_len  CHECK (length(sha256) = 32),
    CONSTRAINT content_size_pos CHECK (byte_size > 0)
);
CREATE INDEX content_taken_idx ON content (taken_at DESC);
CREATE INDEX content_size_idx  ON content (byte_size);

-- A folder the buyer chose. Opened read-only; never written to.
CREATE TABLE scan_root (
    root_id      INTEGER PRIMARY KEY AUTOINCREMENT,
    account_id   INTEGER NOT NULL REFERENCES account ON DELETE CASCADE,
    label        TEXT    NOT NULL,
    abs_path     TEXT    NOT NULL UNIQUE,   -- canonical: forward slashes, no trailing slash
    enabled      INTEGER NOT NULL DEFAULT 1,
    exclude      TEXT    NOT NULL DEFAULT '[]',   -- JSON array of path substrings to skip
    -- Where album folders sit, relative to the root: '' = the root's own
    -- children are albums (the product rule), a path = that subtree's
    -- children, NULL = folders under this root are not albums.
    album_folder TEXT    DEFAULT ''
);

-- Where the bytes are right now. Zero, one or many rows per content: many
-- means the same picture exists in more than one place, which is a fact worth
-- seeing rather than a problem to discover a year later.
CREATE TABLE location (
    location_id INTEGER PRIMARY KEY AUTOINCREMENT,
    content_id  INTEGER NOT NULL REFERENCES content ON DELETE CASCADE,
    account_id  INTEGER NOT NULL REFERENCES account ON DELETE CASCADE,
    root_id     INTEGER NOT NULL REFERENCES scan_root ON DELETE CASCADE,
    rel_path    TEXT    NOT NULL,
    file_mtime  INTEGER,
    first_seen  INTEGER NOT NULL DEFAULT (CAST((julianday('now') - 2440587.5) * 86400000 AS INTEGER)),
    last_seen   INTEGER NOT NULL DEFAULT (CAST((julianday('now') - 2440587.5) * 86400000 AS INTEGER)),
    trashed_at  INTEGER,                    -- a person put it in the bin
    gone_at     INTEGER,                    -- the scanner found it absent
    CONSTRAINT location_path_unique UNIQUE (root_id, rel_path)
);
CREATE INDEX location_content_idx ON location (content_id);
CREATE INDEX location_live_idx    ON location (account_id, content_id)
    WHERE gone_at IS NULL AND trashed_at IS NULL;
CREATE INDEX location_trash_idx   ON location (account_id, trashed_at)
    WHERE trashed_at IS NOT NULL AND gone_at IS NULL;
-- The scanner's hot lookup: "what did I record under this root last time?"
CREATE INDEX location_root_idx    ON location (root_id) WHERE gone_at IS NULL;

-- ---------------------------------------------------------------- albums

CREATE TABLE album (
    album_id         INTEGER PRIMARY KEY AUTOINCREMENT,
    account_id       INTEGER NOT NULL REFERENCES account ON DELETE CASCADE,
    name             TEXT    NOT NULL COLLATE NOCASE,
    description      TEXT    NOT NULL DEFAULT '',
    cover_content_id INTEGER REFERENCES content ON DELETE SET NULL,
    created_at       INTEGER NOT NULL DEFAULT (CAST((julianday('now') - 2440587.5) * 86400000 AS INTEGER)),
    updated_at       INTEGER NOT NULL DEFAULT (CAST((julianday('now') - 2440587.5) * 86400000 AS INTEGER)),
    CONSTRAINT album_name_unique    UNIQUE (account_id, name),
    CONSTRAINT album_name_not_blank CHECK (length(trim(name)) > 0)
);

-- Membership references CONTENT, never location. Filing a picture into an
-- album therefore survives moving it on disk - the property Immich lacked.
CREATE TABLE album_item (
    album_id   INTEGER NOT NULL REFERENCES album ON DELETE CASCADE,
    content_id INTEGER NOT NULL REFERENCES content ON DELETE CASCADE,
    added_at   INTEGER NOT NULL DEFAULT (CAST((julianday('now') - 2440587.5) * 86400000 AS INTEGER)),
    PRIMARY KEY (album_id, content_id)
);
CREATE INDEX album_item_content_idx ON album_item (content_id);

-- Favourites: a person's mark on a picture. Its own fact, because
-- timeline_entry is derived and a row VANISHES when the picture is trashed;
-- a favourite must survive that.
CREATE TABLE favorite (
    account_id INTEGER NOT NULL REFERENCES account ON DELETE CASCADE,
    content_id INTEGER NOT NULL REFERENCES content ON DELETE CASCADE,
    since      INTEGER NOT NULL DEFAULT (CAST((julianday('now') - 2440587.5) * 86400000 AS INTEGER)),
    PRIMARY KEY (account_id, content_id)
);

-- ---------------------------------------------------------------- timeline

-- THE ONE DELIBERATE DENORMALISATION.
--
-- The grid is the hottest query in the product and must stay fast at hundreds
-- of thousands of rows. It needs (account, taken_at) ordering, but `content`
-- cannot carry an account because one picture may live in two, and joining
-- content to location on every scroll is the wrong cost. So this table stores
-- that pair once more, maintained by trigger, with exactly one owner.
CREATE TABLE timeline_entry (
    account_id INTEGER NOT NULL REFERENCES account ON DELETE CASCADE,
    content_id INTEGER NOT NULL REFERENCES content ON DELETE CASCADE,
    taken_at   INTEGER NOT NULL,
    PRIMARY KEY (account_id, content_id)
);
CREATE INDEX timeline_scroll_idx ON timeline_entry (account_id, taken_at DESC, content_id DESC);

-- ---------------------------------------------------------------- derived

-- Keyed on content, so a move never regenerates and two identical files share
-- one file on disk. The PATH IS NOT STORED: it is derived from the hash.
CREATE TABLE thumbnail (
    content_id   INTEGER NOT NULL REFERENCES content ON DELETE CASCADE,
    kind         TEXT    NOT NULL,           -- 'thumb'
    format       TEXT    NOT NULL,
    width        INTEGER NOT NULL,
    height       INTEGER NOT NULL,
    byte_size    INTEGER NOT NULL,
    generated_at INTEGER NOT NULL DEFAULT (CAST((julianday('now') - 2440587.5) * 86400000 AS INTEGER)),
    PRIMARY KEY (content_id, kind),
    CONSTRAINT thumbnail_kind CHECK (kind IN ('thumb', 'preview'))
);

-- Which content the vector index holds, for which model. The vectors
-- themselves live in vectors/<model>.bin (CONTRACT §6); at start the two are
-- reconciled and the numbers are logged, so the file and the ledger cannot
-- drift apart silently.
CREATE TABLE embedding (
    content_id  INTEGER NOT NULL REFERENCES content ON DELETE CASCADE,
    model       TEXT    NOT NULL,
    dims        INTEGER NOT NULL,
    embedded_at INTEGER NOT NULL DEFAULT (CAST((julianday('now') - 2440587.5) * 86400000 AS INTEGER)),
    PRIMARY KEY (content_id, model)
);

-- ---------------------------------------------------------------- operations

-- A run that records seen/added/moved/gone/errors makes "nothing happened"
-- look different from "everything worked". finished_at IS NULL means it is
-- running (or was interrupted: the next start marks those cancelled).
CREATE TABLE scan_run (
    run_id       INTEGER PRIMARY KEY AUTOINCREMENT,
    root_id      INTEGER NOT NULL REFERENCES scan_root ON DELETE CASCADE,
    folder       TEXT,                      -- a scoped scan, or NULL for the whole root
    started_at   INTEGER NOT NULL DEFAULT (CAST((julianday('now') - 2440587.5) * 86400000 AS INTEGER)),
    finished_at  INTEGER,
    files_seen   INTEGER NOT NULL DEFAULT 0,
    added        INTEGER NOT NULL DEFAULT 0,
    moved        INTEGER NOT NULL DEFAULT 0,
    unchanged    INTEGER NOT NULL DEFAULT 0,
    marked_gone  INTEGER NOT NULL DEFAULT 0,
    errors       INTEGER NOT NULL DEFAULT 0,
    error_sample TEXT,
    cancelled    INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX scan_run_root_idx ON scan_run (root_id, started_at DESC);

-- What the watch knows about each folder it minds: the direct children of
-- the album folder (or of the root when there is none). One row per folder,
-- plus one row with folder = '' for the parent itself, whose mtime moves
-- when a child folder appears or goes.
CREATE TABLE scan_folder (
    root_id    INTEGER NOT NULL REFERENCES scan_root ON DELETE CASCADE,
    folder     TEXT    NOT NULL,            -- relative to the root; '' is the parent
    mtime      INTEGER,                     -- as last observed; NULL until swept
    entries    INTEGER,                     -- names in it at the last look
    first_seen INTEGER NOT NULL DEFAULT (CAST((julianday('now') - 2440587.5) * 86400000 AS INTEGER)),
    checked_at INTEGER,                     -- last stat, for the sweep's round-robin
    changed_at INTEGER,                     -- a change noticed and not yet scanned; NULL = in step
    scanned_at INTEGER,
    PRIMARY KEY (root_id, folder)
);
CREATE INDEX scan_folder_sweep_idx   ON scan_folder (root_id, checked_at) WHERE changed_at IS NULL;
CREATE INDEX scan_folder_changed_idx ON scan_folder (changed_at) WHERE changed_at IS NOT NULL;

-- The queue every background job draws from: thumbnails and embeddings. Work
-- is LEASED, not claimed, so a worker that dies mid-job releases it.
CREATE TABLE work_item (
    work_id      INTEGER PRIMARY KEY AUTOINCREMENT,
    kind         TEXT    NOT NULL,
    content_id   INTEGER REFERENCES content ON DELETE CASCADE,
    state        TEXT    NOT NULL DEFAULT 'pending',
    attempts     INTEGER NOT NULL DEFAULT 0,
    last_error   TEXT,
    enqueued_at  INTEGER NOT NULL DEFAULT (CAST((julianday('now') - 2440587.5) * 86400000 AS INTEGER)),
    leased_until INTEGER,
    finished_at  INTEGER,
    CONSTRAINT work_kind  CHECK (kind IN ('thumbnail', 'embedding')),
    CONSTRAINT work_state CHECK (state IN ('pending', 'running', 'done', 'failed'))
);
-- The queue only ever asks for pending work; keep that index the size of the backlog.
CREATE INDEX work_pending_idx ON work_item (kind, enqueued_at) WHERE state = 'pending';
-- One outstanding job per content per kind, enforced rather than hoped for.
CREATE UNIQUE INDEX work_unique_open_idx ON work_item (kind, content_id) WHERE state IN ('pending', 'running');
CREATE INDEX work_content_idx ON work_item (content_id, kind);

-- Pause state and concurrency for the background jobs. Only settings live
-- here; the counts a job reports are derived from the tables that hold the
-- work.
CREATE TABLE job_control (
    name        TEXT    PRIMARY KEY,
    is_paused   INTEGER NOT NULL DEFAULT 0,
    concurrency INTEGER NOT NULL DEFAULT 1,
    updated_at  INTEGER NOT NULL DEFAULT (CAST((julianday('now') - 2440587.5) * 86400000 AS INTEGER)),
    CONSTRAINT job_concurrency_sane CHECK (concurrency BETWEEN 1 AND 64)
);
INSERT INTO job_control (name, concurrency) VALUES ('thumbnailGeneration', 4), ('library', 1), ('embeddings', 1);

-- The few settings the app and the shell persist (CONTRACT §4: settings,
-- search settings, licence state). Keys are dotted, values are text.
CREATE TABLE setting (
    key        TEXT PRIMARY KEY,
    value      TEXT NOT NULL,
    updated_at INTEGER NOT NULL DEFAULT (CAST((julianday('now') - 2440587.5) * 86400000 AS INTEGER))
);
INSERT INTO setting (key, value) VALUES
    ('licence.state', 'free'),
    ('search.enabled', 'true'),
    ('embed.onBattery', 'false');

-- ---------------------------------------------------------------- triggers

-- timeline_entry is derived from location + content. Keeping it correct is a
-- trigger's job, not the application's: an application forgets on the one
-- code path nobody tested. "Live in this account" = at least one location that
-- is neither trashed nor gone, asked fresh rather than inferred from this row
-- alone, because a picture can have several locations in one account.

CREATE TRIGGER location_timeline_sync_insert AFTER INSERT ON location
BEGIN
    INSERT OR REPLACE INTO timeline_entry (account_id, content_id, taken_at)
    SELECT NEW.account_id, NEW.content_id,
           COALESCE((SELECT c.taken_at FROM content c WHERE c.content_id = NEW.content_id),
                    CAST((julianday('now') - 2440587.5) * 86400000 AS INTEGER))
     WHERE NEW.gone_at IS NULL AND NEW.trashed_at IS NULL;
END;

CREATE TRIGGER location_timeline_sync_update
AFTER UPDATE OF content_id, account_id, trashed_at, gone_at ON location
BEGIN
    INSERT OR REPLACE INTO timeline_entry (account_id, content_id, taken_at)
    SELECT NEW.account_id, NEW.content_id,
           COALESCE((SELECT c.taken_at FROM content c WHERE c.content_id = NEW.content_id),
                    CAST((julianday('now') - 2440587.5) * 86400000 AS INTEGER))
     WHERE EXISTS (SELECT 1 FROM location l
                    WHERE l.account_id = NEW.account_id AND l.content_id = NEW.content_id
                      AND l.gone_at IS NULL AND l.trashed_at IS NULL);

    DELETE FROM timeline_entry
     WHERE account_id = NEW.account_id AND content_id = NEW.content_id
       AND NOT EXISTS (SELECT 1 FROM location l
                        WHERE l.account_id = NEW.account_id AND l.content_id = NEW.content_id
                          AND l.gone_at IS NULL AND l.trashed_at IS NULL);

    -- The pair this row used to belong to, when the path now holds other bytes.
    DELETE FROM timeline_entry
     WHERE (OLD.account_id <> NEW.account_id OR OLD.content_id <> NEW.content_id)
       AND account_id = OLD.account_id AND content_id = OLD.content_id
       AND NOT EXISTS (SELECT 1 FROM location l
                        WHERE l.account_id = OLD.account_id AND l.content_id = OLD.content_id
                          AND l.gone_at IS NULL AND l.trashed_at IS NULL);
END;

CREATE TRIGGER location_timeline_sync_delete AFTER DELETE ON location
BEGIN
    DELETE FROM timeline_entry
     WHERE account_id = OLD.account_id AND content_id = OLD.content_id
       AND NOT EXISTS (SELECT 1 FROM location l
                        WHERE l.account_id = OLD.account_id AND l.content_id = OLD.content_id
                          AND l.gone_at IS NULL AND l.trashed_at IS NULL);
END;

-- taken_at can arrive after the location does (metadata is extracted later),
-- so a change to content must reach the timeline too.
CREATE TRIGGER content_timeline_retime AFTER UPDATE OF taken_at ON content
WHEN NEW.taken_at IS NOT OLD.taken_at
BEGIN
    UPDATE timeline_entry
       SET taken_at = COALESCE(NEW.taken_at, CAST((julianday('now') - 2440587.5) * 86400000 AS INTEGER))
     WHERE content_id = NEW.content_id;
END;

CREATE TRIGGER album_item_touch_insert AFTER INSERT ON album_item
BEGIN
    UPDATE album SET updated_at = CAST((julianday('now') - 2440587.5) * 86400000 AS INTEGER)
     WHERE album_id = NEW.album_id;
END;

CREATE TRIGGER album_item_touch_delete AFTER DELETE ON album_item
BEGIN
    UPDATE album SET updated_at = CAST((julianday('now') - 2440587.5) * 86400000 AS INTEGER)
     WHERE album_id = OLD.album_id;
END;

-- ---------------------------------------------------------------- views

-- The two duplicate questions, answered by the schema rather than by a rule:
-- inside one account a byte-identical twin is waste; across accounts it may
-- be deliberate and is only ever reported.
CREATE VIEW duplicate_within_account AS
SELECT account_id, content_id, count(*) AS copies,
       json_group_array(rel_path) AS paths,
       min(location_id) AS keep_location_id
  FROM location
 WHERE gone_at IS NULL AND trashed_at IS NULL
 GROUP BY account_id, content_id
HAVING count(*) > 1;

CREATE VIEW duplicate_across_accounts AS
SELECT content_id,
       json_group_array(DISTINCT account_id) AS accounts,
       count(*) AS copies
  FROM location
 WHERE gone_at IS NULL AND trashed_at IS NULL
 GROUP BY content_id
HAVING count(DISTINCT account_id) > 1;
