PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS schema_version (
    id INTEGER PRIMARY KEY CHECK (id = 1),
    version INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS profiles (
    id INTEGER PRIMARY KEY CHECK (id = 1),
    recipient_name TEXT NOT NULL,
    onboarding_complete INTEGER NOT NULL CHECK (onboarding_complete IN (0, 1))
);

CREATE TABLE IF NOT EXISTS preferences (
    id INTEGER PRIMARY KEY CHECK (id = 1),
    theme INTEGER NOT NULL,
    quiet_hours_enabled INTEGER NOT NULL CHECK (quiet_hours_enabled IN (0, 1)),
    quiet_hours_start TEXT NOT NULL,
    quiet_hours_end TEXT NOT NULL,
    reduced_motion INTEGER NOT NULL CHECK (reduced_motion IN (0, 1)),
    local_note_daily_limit INTEGER NOT NULL,
    launch_at_sign_in INTEGER NOT NULL CHECK (launch_at_sign_in IN (0, 1)),
    always_on_top INTEGER NOT NULL CHECK (always_on_top IN (0, 1)),
    hide_pet_during_fullscreen INTEGER NOT NULL CHECK (hide_pet_during_fullscreen IN (0, 1)),
    ambient_minimum_interval_ticks INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS pet_placements (
    monitor_device_name TEXT PRIMARY KEY,
    normalized_x REAL NOT NULL,
    normalized_y REAL NOT NULL,
    scale REAL NOT NULL
);

CREATE TABLE IF NOT EXISTS reminders (
    id TEXT PRIMARY KEY,
    title TEXT NOT NULL,
    details TEXT NULL,
    enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
    rule_kind INTEGER NOT NULL,
    local_time TEXT NULL,
    weekdays_mask INTEGER NULL,
    interval_ticks INTEGER NULL,
    first_due_utc TEXT NULL,
    local_time_zone_id TEXT NOT NULL,
    quiet_hours_behavior INTEGER NOT NULL,
    missed_policy INTEGER NOT NULL,
    next_due_utc TEXT NULL,
    deliver_after_utc TEXT NULL,
    snoozed_until_utc TEXT NULL,
    quiet_hours_enabled INTEGER NULL,
    quiet_hours_start TEXT NULL,
    quiet_hours_end TEXT NULL
);
CREATE INDEX IF NOT EXISTS ix_reminders_next_due_utc ON reminders(next_due_utc);
CREATE INDEX IF NOT EXISTS ix_reminders_deliver_after_utc ON reminders(deliver_after_utc);
CREATE INDEX IF NOT EXISTS ix_reminders_enabled ON reminders(enabled);

CREATE TABLE IF NOT EXISTS reminder_occurrences (
    reminder_id TEXT NOT NULL REFERENCES reminders(id) ON DELETE CASCADE,
    due_utc TEXT NOT NULL,
    PRIMARY KEY (reminder_id, due_utc)
);

CREATE TABLE IF NOT EXISTS tasks (
    id TEXT PRIMARY KEY,
    title TEXT NOT NULL,
    notes TEXT NULL,
    due_utc TEXT NULL,
    is_completed INTEGER NOT NULL CHECK (is_completed IN (0, 1)),
    created_utc TEXT NOT NULL,
    updated_utc TEXT NOT NULL,
    completed_utc TEXT NULL
);
CREATE INDEX IF NOT EXISTS ix_tasks_active ON tasks(is_completed, updated_utc);

CREATE TABLE IF NOT EXISTS focus_sessions (
    id TEXT PRIMARY KEY,
    task_id TEXT NULL REFERENCES tasks(id) ON DELETE SET NULL,
    started_utc TEXT NOT NULL,
    ends_utc TEXT NULL,
    remaining_when_paused_ticks INTEGER NOT NULL,
    status INTEGER NOT NULL,
    updated_utc TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_focus_sessions_active ON focus_sessions(status);

CREATE TABLE IF NOT EXISTS local_notes (
    id TEXT PRIMARY KEY,
    text TEXT NOT NULL UNIQUE,
    enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
    is_default INTEGER NOT NULL DEFAULT 0 CHECK (is_default IN (0, 1))
);

CREATE TABLE IF NOT EXISTS local_note_history (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    note_id TEXT NOT NULL REFERENCES local_notes(id) ON DELETE CASCADE,
    shown_utc TEXT NOT NULL,
    local_date TEXT NOT NULL,
    unsolicited INTEGER NOT NULL CHECK (unsolicited IN (0, 1))
);
CREATE INDEX IF NOT EXISTS ix_local_note_history_recent ON local_note_history(shown_utc DESC);
CREATE INDEX IF NOT EXISTS ix_local_note_history_daily ON local_note_history(local_date, unsolicited);

CREATE TABLE IF NOT EXISTS mood_check_ins (
    id TEXT PRIMARY KEY,
    choice INTEGER NOT NULL,
    note TEXT NULL,
    created_utc TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_mood_check_ins_created ON mood_check_ins(created_utc DESC);

CREATE TABLE IF NOT EXISTS countdowns (
    id TEXT PRIMARY KEY,
    title TEXT NOT NULL,
    target_utc TEXT NULL,
    target_date TEXT NULL,
    is_all_day INTEGER NOT NULL CHECK (is_all_day IN (0, 1)),
    local_time_zone_id TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS remote_envelopes (
    message_id TEXT PRIMARY KEY,
    ciphertext BLOB NOT NULL,
    ephemeral_public_key BLOB NULL,
    nonce BLOB NULL,
    authentication_tag BLOB NULL,
    deliver_after_utc TEXT NULL,
    received_utc TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_remote_envelopes_deliver_after ON remote_envelopes(deliver_after_utc);

CREATE TABLE IF NOT EXISTS processed_remote_messages (
    message_id TEXT PRIMARY KEY,
    processed_utc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS asset_packs (
    pack_id TEXT PRIMARY KEY,
    version TEXT NOT NULL,
    manifest_path TEXT NOT NULL,
    attribution TEXT NULL,
    private_use_only INTEGER NOT NULL CHECK (private_use_only IN (0, 1)),
    selected INTEGER NOT NULL CHECK (selected IN (0, 1))
);

INSERT INTO schema_version (id, version) VALUES (1, 1)
ON CONFLICT(id) DO NOTHING;
