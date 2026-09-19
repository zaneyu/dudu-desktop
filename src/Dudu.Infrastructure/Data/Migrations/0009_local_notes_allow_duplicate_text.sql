-- Rebuilds local_notes without the UNIQUE constraint on text. Two jar notes
-- can legitimately share the same words (e.g. "i love u" saved twice); the
-- old UNIQUE(text) made the second save fail every retry, leaving it stuck
-- pending forever, since LocalNoteRepository.SaveToJarAsync only resolves
-- ON CONFLICT(id).
--
-- local_note_history.note_id REFERENCES local_notes(id) ON DELETE CASCADE.
-- With FK enforcement on, DROP TABLE local_notes below performs an implicit
-- DELETE FROM local_notes first, which DOES fire that cascade and would
-- silently empty local_note_history. MigrationRunner.RunAsync wraps every
-- migration's transaction with PRAGMA foreign_keys=OFF beforehand and a
-- foreign_key_check before commit (SQLite's documented procedure for this
-- kind of rebuild: https://www.sqlite.org/lang_altertable.html#otheralter),
-- which is what actually keeps local_note_history intact here -- not any
-- inherent property of DROP TABLE.
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
