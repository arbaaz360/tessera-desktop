-- What is playing in a video, as a fact Tessera owns.
--
-- The music sidecar kept its results in a schema inside Immich's database,
-- keyed on Immich's asset uuid and foreign-keyed to Immich's tables. Neither
-- survives Immich, and neither could answer "how many videos have been
-- identified" here. Contract: docs/contracts/music.md.
--
-- One row per video in content_track WHETHER OR NOT it matched. "Asked and
-- answered" is the fact worth keeping: it is what stops a video being sent to
-- the identifier again and again, and what lets the jobs page say processed /
-- never / found an answer.

CREATE TABLE track (
    track_id          bigint      PRIMARY KEY GENERATED ALWAYS AS IDENTITY,
    -- The sidecar's key, kept so links and imported rows stay stable: a hash
    -- of the provider's id, or of artist+title when it gave none.
    key               text        NOT NULL UNIQUE,
    title             text        NOT NULL,
    artist            text        NOT NULL DEFAULT '',
    album             text,
    artwork_url       text,
    provider          text        NOT NULL DEFAULT '',
    provider_track_id text,
    raw               jsonb       NOT NULL DEFAULT '{}'::jsonb,
    first_seen_at     timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE content_track (
    content_id     bigint      PRIMARY KEY REFERENCES content ON DELETE CASCADE,
    track_id       bigint      REFERENCES track ON DELETE SET NULL,
    matched        boolean     NOT NULL,
    offset_seconds real,
    confidence     real,
    identifier     jsonb       NOT NULL DEFAULT '{}'::jsonb,   -- which service, which version
    identified_at  timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT content_track_match_has_track CHECK (NOT matched OR track_id IS NOT NULL)
);

CREATE INDEX content_track_track_idx ON content_track (track_id) WHERE track_id IS NOT NULL;

-- The third job on the jobs page.
INSERT INTO job_control (name) VALUES ('music') ON CONFLICT (name) DO NOTHING;
