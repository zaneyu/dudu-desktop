-- The tray/overlay pause used to live only in memory and was lost on every
-- restart. pause_mode is the app's pause mode name (NULL = not paused);
-- pause_expires_utc is its end for a timed pause.
ALTER TABLE preferences ADD COLUMN pause_mode TEXT NULL;
ALTER TABLE preferences ADD COLUMN pause_expires_utc TEXT NULL;
