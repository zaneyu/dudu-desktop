-- Backfills the seed_state watermark (0007_seed_watermark.sql) for
-- databases upgrading from a schema version that predates it. Without this,
-- a database from an older build looks "never seeded" to SeedData.SeedAsync,
-- which would then resurrect default notes the user deliberately deleted --
-- and, combined with the old UNIQUE(text) on local_notes (see 0009), could
-- fail db-init outright on a text collision.
--
-- "Used install" is EXISTS across local_notes, profiles, preferences,
-- reminders and tasks -- not just local_notes. A user who deleted every
-- single note (including all shipped defaults) before upgrading still has
-- rows in at least one of the others from actually using the app, and
-- checking local_notes alone would misclassify that install as fresh and
-- resurrect the deleted defaults anyway. None of these five tables is
-- written by any migration or by SeedData.SeedAsync on a fresh install, so a
-- genuinely brand-new database still reaches this migration with all of them
-- empty and is correctly left for the normal first-run seeding path.
INSERT INTO seed_state (id, version)
SELECT 1, 1
WHERE NOT EXISTS (SELECT 1 FROM seed_state WHERE id = 1)
  AND (
    EXISTS (SELECT 1 FROM local_notes)
    OR EXISTS (SELECT 1 FROM profiles)
    OR EXISTS (SELECT 1 FROM preferences)
    OR EXISTS (SELECT 1 FROM reminders)
    OR EXISTS (SELECT 1 FROM tasks)
  );
