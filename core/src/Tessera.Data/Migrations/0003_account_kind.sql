-- What kind of account this is: a person's library, or a corpus.
--
-- The corpus accounts (reddit, simpcity) hold hundreds of thousands of files
-- the owner browses but did not take and does not curate: no thumbnails are
-- generated for them, no cover suggestions are ranked for them. Until now
-- that was true only because nobody had asked - the thumbnail queue would
-- take them happily, and on 2026-09-11 it did, 852,227 of them, from a test.
-- A rule that is only true while nobody presses the button is not a rule.
--
-- One column, one declaration. Anything that must treat a corpus differently
-- asks this and nothing else; adding a corpus is one UPDATE.
ALTER TABLE account
    ADD COLUMN IF NOT EXISTS kind text NOT NULL DEFAULT 'personal'
    CONSTRAINT account_kind_known CHECK (kind IN ('personal', 'corpus'));

UPDATE account SET kind = 'corpus' WHERE email IN ('reddit@gmail.com', 'simpcity@gmail.com');
