-- ==========================================
-- Migration 029: Case-insensitive username uniqueness
-- ==========================================
-- users_username_active_key (migration 003) was case-sensitive, so "jsmith" and "JSmith" could
-- exist as two distinct active accounts -- login/matching queries everywhere (POST /api/login,
-- POST /api/kiosk-auth, the dashboard login, the bulk CSV user import) already compare
-- case-insensitively, so this was purely a gap in what the database would let two different
-- people register as, not something anyone could exploit through a single login attempt.
--
-- Replaces the index with a case-insensitive one, same pattern as tools.cal_id (migration 028):
-- LOWER() in the index itself rather than forcing every stored username to lowercase, so a
-- person's originally-typed casing is still preserved for display. New/renamed usernames are
-- also now lowercased going forward (see POST /api/users and PUT /api/users/me/update) so this
-- can't recur, but the index is what actually enforces it regardless of which code path writes.

DROP INDEX IF EXISTS users_username_active_key;
CREATE UNIQUE INDEX IF NOT EXISTS users_username_active_key ON users (LOWER(username)) WHERE is_active = true;
