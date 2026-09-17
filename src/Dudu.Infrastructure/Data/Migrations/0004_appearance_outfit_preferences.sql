ALTER TABLE preferences ADD COLUMN outfit_key TEXT NULL;
ALTER TABLE preferences ADD COLUMN automatic_seasonal_mode INTEGER NOT NULL DEFAULT 1;
ALTER TABLE preferences ADD COLUMN anniversary_month INTEGER NULL;
ALTER TABLE preferences ADD COLUMN anniversary_day INTEGER NULL;
ALTER TABLE preferences ADD COLUMN birthday_month INTEGER NULL;
ALTER TABLE preferences ADD COLUMN birthday_day INTEGER NULL;
