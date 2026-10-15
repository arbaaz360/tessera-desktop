-- Tessera schema.
--
-- ONE IDEA RUNS THROUGH ALL OF IT: a picture is identified by its bytes, never
-- by where it sits. Immich identified an external asset by path, so moving a
-- file minted a new asset and abandoned the old one - which on 2026-09-09/10
-- produced 122,000 broken embedding links, 150,511 stale face rows, 340,739
-- orphaned thumbnails, and a duplicate double-write that went unnoticed for ten
-- months. Here a move is one UPDATE and nothing downstream notices.
--
-- No vectors live here. Foundation owns every embedding, FaceSearch owns every
-- face, and both join to this database on the sha256.

BEGIN;

CREATE EXTENSION IF NOT EXISTS citext;

-- ---------------------------------------------------------------- accounts

CREATE TABLE account (
    account_id    bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    email         citext      NOT NULL UNIQUE,
    display_name  text        NOT NULL,
    password_hash text        NOT NULL,
    is_admin      boolean     NOT NULL DEFAULT false,
    created_at    timestamptz NOT NULL DEFAULT now(),
    disabled_at   timestamptz,
    CONSTRAINT account_name_not_blank CHECK (length(btrim(display_name)) > 0)
);

-- Tokens are stored hashed, so a database dump is not a set of live
-- credentials. The token itself exists only in the client's cookie.
CREATE TABLE session (
    session_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    account_id bigint      NOT NULL REFERENCES account ON DELETE CASCADE,
    token_hash bytea       NOT NULL UNIQUE,
    user_agent text        NOT NULL DEFAULT '',
    created_at timestamptz NOT NULL DEFAULT now(),
    expires_at timestamptz NOT NULL,
    last_seen  timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX session_account_idx ON session (account_id);
CREATE INDEX session_expiry_idx  ON session (expires_at);

CREATE TABLE api_key (
    api_key_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    account_id bigint      NOT NULL REFERENCES account ON DELETE CASCADE,
    name       text        NOT NULL,
    key_hash   bytea       NOT NULL UNIQUE,
    created_at timestamptz NOT NULL DEFAULT now(),
    last_used  timestamptz,
    revoked_at timestamptz
);
CREATE INDEX api_key_account_idx ON api_key (account_id) WHERE revoked_at IS NULL;

-- ---------------------------------------------------------------- content

-- One row per distinct set of bytes, for ever. Nothing in this table changes
-- when a file moves, which is the entire point.
CREATE TABLE content (
    content_id  bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    sha256      bytea       NOT NULL UNIQUE,
    byte_size   bigint      NOT NULL,
    mime        text        NOT NULL,
    width       int,
    height      int,
    duration_ms int,
    taken_at    timestamptz,
    taken_src   text,                       -- 'exif' | 'filename' | 'mtime'
    first_seen  timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT content_sha_len   CHECK (octet_length(sha256) = 32),
    CONSTRAINT content_size_pos  CHECK (byte_size > 0)
);
CREATE INDEX content_taken_idx ON content (taken_at DESC NULLS LAST);
CREATE INDEX content_size_idx  ON content (byte_size);

-- Where the bytes are right now. Zero, one or many rows per content: many
-- means the same picture exists in more than one place, which is a fact worth
-- seeing rather than a problem to discover a year later.
CREATE TABLE scan_root (
    root_id    bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    account_id bigint  NOT NULL REFERENCES account ON DELETE CASCADE,
    label      text    NOT NULL,
    abs_path   text    NOT NULL UNIQUE,     -- container path, e.g. /reddit
    enabled    boolean NOT NULL DEFAULT true,
    exclude    text[]  NOT NULL DEFAULT '{}'
);

CREATE TABLE location (
    location_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    content_id  bigint      NOT NULL REFERENCES content ON DELETE CASCADE,
    account_id  bigint      NOT NULL REFERENCES account ON DELETE CASCADE,
    root_id     bigint      NOT NULL REFERENCES scan_root ON DELETE CASCADE,
    rel_path    text        NOT NULL,
    file_mtime  timestamptz,
    first_seen  timestamptz NOT NULL DEFAULT now(),
    last_seen   timestamptz NOT NULL DEFAULT now(),
    trashed_at  timestamptz,                -- a person put it in the bin
    gone_at     timestamptz,                -- the scanner found it absent
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
    album_id         bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    account_id       bigint      NOT NULL REFERENCES account ON DELETE CASCADE,
    name             citext      NOT NULL,
    description      text        NOT NULL DEFAULT '',
    cover_content_id bigint      REFERENCES content ON DELETE SET NULL,
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT album_name_unique   UNIQUE (account_id, name),
    CONSTRAINT album_name_not_blank CHECK (length(btrim(name::text)) > 0)
);

-- Membership references CONTENT, never location. Filing a picture into an
-- album therefore survives moving it on disk - the property Immich lacked.
CREATE TABLE album_item (
    album_id   bigint      NOT NULL REFERENCES album ON DELETE CASCADE,
    content_id bigint      NOT NULL REFERENCES content ON DELETE CASCADE,
    added_at   timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (album_id, content_id)
);
CREATE INDEX album_item_content_idx ON album_item (content_id);

-- ---------------------------------------------------------------- timeline

-- THE ONE DELIBERATE DENORMALISATION.
--
-- The grid is the hottest query in the product and must stay fast at millions
-- of rows. It needs (account, taken_at) ordering, but `content` cannot carry
-- an account because one picture may live in two, and joining content to
-- location on every scroll is the wrong cost. So this table stores that pair
-- once more, maintained by trigger, with exactly one owner.
--
-- It is written down as a duplication on purpose. Every other fact in this
-- schema lives in one place.
CREATE TABLE timeline_entry (
    account_id bigint      NOT NULL REFERENCES account ON DELETE CASCADE,
    content_id bigint      NOT NULL REFERENCES content ON DELETE CASCADE,
    taken_at   timestamptz NOT NULL,
    PRIMARY KEY (account_id, content_id)
);
CREATE INDEX timeline_scroll_idx ON timeline_entry (account_id, taken_at DESC, content_id DESC);

-- ---------------------------------------------------------------- derived

-- Keyed on content, so a move never regenerates and two identical files share
-- one file on disk. The PATH IS NOT STORED: it is derived from the hash. A
-- stored path is a second copy of a fact that drifts from the first, which is
-- how 340,739 orphaned thumbnails accumulated in the system this replaces.
CREATE TABLE thumbnail (
    content_id   bigint      NOT NULL REFERENCES content ON DELETE CASCADE,
    kind         text        NOT NULL,      -- 'thumb' | 'preview'
    format       text        NOT NULL,
    width        int         NOT NULL,
    height       int         NOT NULL,
    byte_size    bigint      NOT NULL,
    generated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (content_id, kind),
    CONSTRAINT thumbnail_kind CHECK (kind IN ('thumb', 'preview'))
);

-- ---------------------------------------------------------------- operations

-- Exists because the worst failures of the system this replaces were SILENT:
-- four containers reported "Up" for eighteen days while every run died in
-- configuration parsing. A run that records seen/added/moved/gone/errors makes
-- "nothing happened" look different from "everything worked".
CREATE TABLE scan_run (
    run_id       bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    root_id      bigint      NOT NULL REFERENCES scan_root ON DELETE CASCADE,
    started_at   timestamptz NOT NULL DEFAULT now(),
    finished_at  timestamptz,
    files_seen   bigint      NOT NULL DEFAULT 0,
    added        bigint      NOT NULL DEFAULT 0,
    moved        bigint      NOT NULL DEFAULT 0,
    unchanged    bigint      NOT NULL DEFAULT 0,
    marked_gone  bigint      NOT NULL DEFAULT 0,
    errors       bigint      NOT NULL DEFAULT 0,
    error_sample text,
    cancelled    boolean     NOT NULL DEFAULT false
);
CREATE INDEX scan_run_root_idx ON scan_run (root_id, started_at DESC);

CREATE TABLE work_item (
    work_id     bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    kind        text        NOT NULL,
    content_id  bigint      REFERENCES content ON DELETE CASCADE,
    state       text        NOT NULL DEFAULT 'pending',
    attempts    int         NOT NULL DEFAULT 0,
    last_error  text,
    enqueued_at timestamptz NOT NULL DEFAULT now(),
    leased_until timestamptz,
    finished_at timestamptz,
    CONSTRAINT work_state CHECK (state IN ('pending', 'running', 'done', 'failed'))
);
-- Partial index: the queue only ever asks for pending work, and this keeps the
-- index the size of the backlog rather than the size of all history.
CREATE INDEX work_pending_idx ON work_item (kind, enqueued_at)
    WHERE state = 'pending';
-- One outstanding job per content per kind, enforced rather than hoped for.
CREATE UNIQUE INDEX work_unique_open_idx ON work_item (kind, content_id)
    WHERE state IN ('pending', 'running');

-- ---------------------------------------------------------------- triggers

-- timeline_entry is derived from location + content. Keeping it correct is a
-- trigger's job, not the application's: an application forgets on the one code
-- path nobody tested.
CREATE OR REPLACE FUNCTION timeline_sync() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    v_taken timestamptz;
    v_live  boolean;
BEGIN
    SELECT c.taken_at INTO v_taken FROM content c
     WHERE c.content_id = COALESCE(NEW.content_id, OLD.content_id);

    -- Live in this account = at least one location that is neither trashed nor
    -- gone. Asked fresh rather than inferred from this row alone, because a
    -- picture can have several locations in one account.
    SELECT EXISTS (
        SELECT 1 FROM location l
         WHERE l.account_id = COALESCE(NEW.account_id, OLD.account_id)
           AND l.content_id = COALESCE(NEW.content_id, OLD.content_id)
           AND l.gone_at IS NULL AND l.trashed_at IS NULL
    ) INTO v_live;

    IF v_live THEN
        INSERT INTO timeline_entry (account_id, content_id, taken_at)
        VALUES (COALESCE(NEW.account_id, OLD.account_id),
                COALESCE(NEW.content_id, OLD.content_id),
                COALESCE(v_taken, now()))
        ON CONFLICT (account_id, content_id)
        DO UPDATE SET taken_at = EXCLUDED.taken_at;
    ELSE
        DELETE FROM timeline_entry
         WHERE account_id = COALESCE(NEW.account_id, OLD.account_id)
           AND content_id = COALESCE(NEW.content_id, OLD.content_id);
    END IF;

    RETURN NULL;
END $$;

CREATE TRIGGER location_timeline_sync
AFTER INSERT OR UPDATE OF content_id, account_id, trashed_at, gone_at OR DELETE
ON location FOR EACH ROW EXECUTE FUNCTION timeline_sync();

-- taken_at can arrive after the location does (metadata is extracted later),
-- so a change to content must reach the timeline too.
CREATE OR REPLACE FUNCTION timeline_retime() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    UPDATE timeline_entry SET taken_at = COALESCE(NEW.taken_at, now())
     WHERE content_id = NEW.content_id;
    RETURN NULL;
END $$;

CREATE TRIGGER content_timeline_retime
AFTER UPDATE OF taken_at ON content
FOR EACH ROW WHEN (NEW.taken_at IS DISTINCT FROM OLD.taken_at)
EXECUTE FUNCTION timeline_retime();

CREATE OR REPLACE FUNCTION touch_album() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    UPDATE album SET updated_at = now()
     WHERE album_id = COALESCE(NEW.album_id, OLD.album_id);
    RETURN NULL;
END $$;

CREATE TRIGGER album_item_touch
AFTER INSERT OR DELETE ON album_item
FOR EACH ROW EXECUTE FUNCTION touch_album();

-- ---------------------------------------------------------------- views

-- The two duplicate questions the owner asked for, answered by the schema
-- rather than by a rule: inside one account a byte-identical twin is waste and
-- may be removed automatically; across accounts it may be deliberate and is
-- only ever reported.
CREATE VIEW duplicate_within_account AS
SELECT account_id, content_id, count(*) AS copies,
       array_agg(rel_path ORDER BY first_seen) AS paths,
       min(location_id) AS keep_location_id
FROM   location
WHERE  gone_at IS NULL AND trashed_at IS NULL
GROUP  BY account_id, content_id
HAVING count(*) > 1;

CREATE VIEW duplicate_across_accounts AS
SELECT content_id,
       array_agg(DISTINCT account_id) AS accounts,
       count(*) AS copies
FROM   location
WHERE  gone_at IS NULL AND trashed_at IS NULL
GROUP  BY content_id
HAVING count(DISTINCT account_id) > 1;

COMMIT;
