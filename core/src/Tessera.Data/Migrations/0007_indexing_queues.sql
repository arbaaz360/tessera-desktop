-- Two more switches on the jobs page, for the GPU work that lives in other
-- services: faces (FaceSearch's ingest requests) and embeddings (Foundation's
-- resident runner). Neither is a queue Tessera holds - the queues are theirs -
-- but the START is a person's and it happens here, so each gets a row like
-- the others. is_paused = true means "not draining": the drain switches
-- itself back to paused when nothing is left, so Run is one press per batch
-- of folders, not a standing order.
INSERT INTO job_control (name, is_paused, concurrency)
VALUES ('faces', true, 1), ('embeddings', true, 1)
ON CONFLICT (name) DO NOTHING;
