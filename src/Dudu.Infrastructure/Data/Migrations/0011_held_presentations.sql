-- Durable backing for PresentationPolicy's in-memory held queue: a reminder,
-- remote-note arrival, or local note suppressed by quiet hours, fullscreen, a
-- locked session, or a pause used to sit only in memory (PresentationPolicy's
-- Queue<DurableNotification>), so quitting or crashing while an item was held
-- lost it silently. A row exists for as long as the matching item is held or
-- being presented: PresentationCoordinator writes it on enqueue and deletes
-- it once the item leaves the queue for good -- presented, purged as
-- expired, dropped as abandoned after 7 days, or its source deleted (see the
-- cascading deletes in LocalNoteRepository, ReminderRepository, and
-- RemoteEnvelopeRepository). A retried-but-still-held item keeps its row
-- and original queued_utc rather than being re-persisted. Remote notes carry
-- only their message id (see DurableNotification.RemoteNote); a reminder or
-- local note's title/body/animation key is copied here, but that is already
-- plaintext the user has stored locally in reminders/local_notes.
CREATE TABLE IF NOT EXISTS held_presentations (
    presentation_key TEXT PRIMARY KEY,
    kind TEXT NOT NULL,
    item_id TEXT NOT NULL,
    title TEXT NULL,
    body TEXT NULL,
    animation_key TEXT NULL,
    expires_utc TEXT NULL,
    queued_utc TEXT NOT NULL,
    toasted INTEGER NOT NULL DEFAULT 0
);
