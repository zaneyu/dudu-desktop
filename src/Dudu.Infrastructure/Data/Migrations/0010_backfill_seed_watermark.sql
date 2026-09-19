-- Backfills the seed_state watermark (0007_seed_watermark.sql) for
-- databases upgrading from a schema version that predates it. Without this,
-- a database that already has local_notes rows from an older build looks
-- "never seeded" to SeedData.SeedAsync, which would then resurrect default
-- notes the user deliberately deleted -- and, combined with the old
-- UNIQUE(text) on local_notes (see 0009), could fail db-init outright on a
-- text collision. Only backfills when local_notes already has rows: a
-- brand-new database reaches this migration with an empty local_notes
-- table (SeedData runs after migrations), so it is left for the normal
-- first-run seeding path.
INSERT INTO seed_state (id, version)
SELECT 1, 1
WHERE NOT EXISTS (SELECT 1 FROM seed_state WHERE id = 1)
  AND EXISTS (SELECT 1 FROM local_notes);
