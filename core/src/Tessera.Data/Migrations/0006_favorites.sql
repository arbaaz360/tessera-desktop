-- Favourites: a person's mark on a picture, in their own account.
--
-- Not a column on timeline_entry: that table is derived by trigger from
-- location + content and a row VANISHES when the picture is trashed or every
-- copy goes. A favourite must survive that - restore the picture and it is
-- still starred - so it is its own fact, keyed the same way.
--
-- Immich's favourites did not come over in the import (no column carried
-- them); this table starts empty.
CREATE TABLE favorite (
    account_id bigint      NOT NULL REFERENCES account ON DELETE CASCADE,
    content_id bigint      NOT NULL REFERENCES content ON DELETE CASCADE,
    since      timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (account_id, content_id)
);
