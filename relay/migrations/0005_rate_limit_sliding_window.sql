-- Two-window sliding approximation for rate limits: keep the previous hourly window's count so a
-- caller cannot spend a full allowance at the end of one hour and another at the start of the
-- next. Existing rows simply start with no previous-window history.
ALTER TABLE rate_limit_buckets ADD COLUMN previous_count INTEGER NOT NULL DEFAULT 0;
