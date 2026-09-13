ALTER TABLE remote_envelopes ADD COLUMN hkdf_salt BLOB NULL;
ALTER TABLE remote_envelopes ADD COLUMN created_utc TEXT NULL;
