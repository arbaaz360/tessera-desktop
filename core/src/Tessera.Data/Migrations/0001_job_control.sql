-- Pause state and concurrency for the background jobs.
--
-- Only settings live here. The COUNTS a job reports are derived from the
-- tables that already hold the work - work_item for thumbnails, scan_run for
-- the library scan - because a counter maintained alongside the work is a
-- second copy of it, and the two drift the first time a process dies between
-- doing the work and incrementing the number.

CREATE TABLE IF NOT EXISTS job_control (
    name        text PRIMARY KEY,
    is_paused   boolean     NOT NULL DEFAULT false,
    concurrency int         NOT NULL DEFAULT 1,
    updated_at  timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT job_concurrency_sane CHECK (concurrency BETWEEN 1 AND 64)
);

-- The two jobs Tessera actually has. Immich declares nineteen queues; this
-- system does two things in the background, and claiming otherwise would put
-- controls on a page that move nothing.
INSERT INTO job_control (name) VALUES ('thumbnailGeneration'), ('library')
ON CONFLICT (name) DO NOTHING;
