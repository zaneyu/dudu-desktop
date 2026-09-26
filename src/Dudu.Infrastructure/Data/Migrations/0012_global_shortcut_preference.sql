-- Persists the global show/hide shortcut chosen on the Appearance page. It
-- used to live only in the running GlobalHotkeyService, so every restart
-- silently re-registered Ctrl+Alt+D even though the page had said "shortcut
-- set". NULL means "use the built-in default" (existing installs keep it).
ALTER TABLE preferences ADD COLUMN global_shortcut TEXT NULL;
