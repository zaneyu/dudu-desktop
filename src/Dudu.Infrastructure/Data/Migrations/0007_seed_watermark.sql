-- Tracks one-time seeding so user-deleted default notes are not resurrected
-- on every launch. SeedData.SeedAsync inserts the bundled defaults only when
-- this watermark is absent, then records it in the same transaction.
CREATE TABLE IF NOT EXISTS seed_state (
    id INTEGER PRIMARY KEY CHECK (id = 1),
    version INTEGER NOT NULL DEFAULT 1
);
