-- The custom global shortcut used to live only in the running hotkey
-- service, so startup always re-registered Ctrl+Alt+D. NULL = the default.
ALTER TABLE preferences ADD COLUMN global_shortcut TEXT NULL;
-- The tray/overlay pause used to live only in memory and was lost on every
-- restart. pause_mode is the app's pause mode name (NULL = not paused);
-- pause_expires_utc is its end for a timed pause.
ALTER TABLE preferences ADD COLUMN pause_mode TEXT NULL;
ALTER TABLE preferences ADD COLUMN pause_expires_utc TEXT NULL;
