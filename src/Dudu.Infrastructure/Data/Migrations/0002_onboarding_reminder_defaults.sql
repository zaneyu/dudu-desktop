ALTER TABLE preferences ADD COLUMN hydration_reminders_enabled INTEGER NOT NULL DEFAULT 1;
ALTER TABLE preferences ADD COLUMN break_reminders_enabled INTEGER NOT NULL DEFAULT 1;
