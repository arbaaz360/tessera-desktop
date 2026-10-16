-- Capture dates from the files' own headers, and copies told apart from moves.

-- Which header reader last looked at this content: 0 = none yet. The scanner
-- reads headers when it first hashes a content and stamps the reader's
-- version; the backfill reads the rest (everything indexed before this
-- migration) in content_id order, so it resumes where it stopped after a
-- restart without keeping a cursor anywhere. A better reader later bumps the
-- version and the same pass reaches the existing library.
ALTER TABLE content ADD COLUMN meta_version INTEGER NOT NULL DEFAULT 0;

-- The backfill's queue: the size of what is left, and empty once it is done.
CREATE INDEX content_meta_pending_idx ON content (content_id) WHERE meta_version < 1;

-- A known content found at a new path while another live copy of it is
-- still on disk. Moves stay in `moved`: the same bytes at a new path, the
-- old path gone.
ALTER TABLE scan_run ADD COLUMN copies INTEGER NOT NULL DEFAULT 0;
