-- Device identity, one-time pairing, sender sessions, and rate-limit buckets.
-- No plaintext secrets are ever stored here: only hashes of capability tokens and pairing
-- codes. All `*_utc` columns hold UTC ISO-8601 `Z` strings.

CREATE TABLE devices (
  id TEXT PRIMARY KEY,
  public_key_spki TEXT NOT NULL,
  desktop_token_hash TEXT NOT NULL UNIQUE,
  created_utc TEXT NOT NULL,
  revoked_utc TEXT
);

CREATE TABLE pairing_codes (
  code_hash TEXT PRIMARY KEY,
  device_id TEXT NOT NULL REFERENCES devices (id),
  expires_utc TEXT NOT NULL,
  consumed_utc TEXT,
  attempt_count INTEGER NOT NULL DEFAULT 0
);

CREATE INDEX idx_pairing_codes_device_id ON pairing_codes (device_id);

CREATE TABLE sender_sessions (
  id TEXT PRIMARY KEY,
  device_id TEXT NOT NULL REFERENCES devices (id),
  token_hash TEXT NOT NULL UNIQUE,
  created_utc TEXT NOT NULL,
  expires_utc TEXT NOT NULL,
  revoked_utc TEXT
);

CREATE INDEX idx_sender_sessions_device_id ON sender_sessions (device_id);

CREATE TABLE rate_limit_buckets (
  key_hash TEXT PRIMARY KEY,
  window_start_utc TEXT NOT NULL,
  count INTEGER NOT NULL DEFAULT 0
);
