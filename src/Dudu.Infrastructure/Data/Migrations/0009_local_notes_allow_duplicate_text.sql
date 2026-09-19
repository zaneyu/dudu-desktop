-- Rebuilds local_notes without the UNIQUE constraint on text. Two jar notes
-- can legitimately share the same words (e.g. "i love u" saved twice); the
-- old UNIQUE(text) made the second save fail every retry, leaving it stuck
-- pending forever, since LocalNoteRepository.SaveToJarAsync only resolves
-- ON CONFLICT(id). local_note_history's FK to local_notes(id) is defined on
-- the child table, not this one, so it is unaffected by rebuilding this
-- table under it (SQLite re-resolves the reference once the replacement is
-- renamed into place) and DROP TABLE does not trigger FK actions.
CREATE TABLE new_local_notes (
    id TEXT PRIMARY KEY,
    text TEXT NOT NULL,
    enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
    is_default INTEGER NOT NULL DEFAULT 0 CHECK (is_default IN (0, 1))
);

INSERT INTO new_local_notes (id, text, enabled, is_default)
SELECT id, text, enabled, is_default FROM local_notes;

DROP TABLE local_notes;

ALTER TABLE new_local_notes RENAME TO local_notes;
