-- Durable backing for PresentationPolicy's in-memory held queue: a reminder,
-- remote-note arrival, or local note suppressed by quiet hours, fullscreen, a
-- locked session, or a pause used to sit only in memory (PresentationPolicy's
-- Queue<DurableNotification>), so quitting or crashing while an item was held
-- lost it silently. A row exists for exactly as long as the matching item
-- sits in the in-memory queue: PresentationCoordinator writes it on enqueue
-- and deletes it the moment the item leaves the queue (released for
-- presentation, requeued after a failed attempt re-adds it, or purged as
-- expired). Remote notes carry only their message id (see
-- DurableNotification.RemoteNote): no note content is ever persisted here.
CREATE TABLE IF NOT EXISTS held_presentations (
    presentation_key TEXT PRIMARY KEY,
    kind TEXT NOT NULL,
    item_id TEXT NOT NULL,
    title TEXT NULL,
    body TEXT NULL,
    animation_key TEXT NULL,
    expires_utc TEXT NULL,
    queued_utc TEXT NOT NULL
);
