# Workspace migration safety — 20260528130716_AddWorkspaceForeignKeysAndUniques

This note covers operational safety for the migration that adds workspace
foreign keys and per-workspace unique indexes. **Read this before running
`dotnet ef database update` on any database that holds data you care about.**

## What the migration does

1. **Defensive `DELETE`s** — runs before adding the FKs. Removes any row whose
   `WorkspaceId` does not point at an existing `Workspaces.Id`, plus stray
   `WorkspaceMembers` whose `WorkspaceId` / `UserId` is dangling, plus
   `Workspaces` whose `OwnerUserId` is dangling. This is a safety net so the
   subsequent `ALTER TABLE ... ADD FOREIGN KEY` doesn't fail.
2. **Foreign keys**, all `RESTRICT` except `WorkspaceMembers` (`CASCADE`):
   - `Posts.WorkspaceId`, `PostMediaItems.WorkspaceId`, `Media.WorkspaceId`,
     `MetaConnections.WorkspaceId`, `ConnectedPages.WorkspaceId`,
     `ConnectedInstagramAccounts.WorkspaceId`, `AiVoiceProfiles.WorkspaceId`,
     `MetaOAuthStates.WorkspaceId` → `Workspaces.Id` (`RESTRICT`)
   - `WorkspaceMembers.WorkspaceId` → `Workspaces.Id` (`CASCADE`)
   - `WorkspaceMembers.UserId` → `AppUsers.Id` (`CASCADE`)
   - `Workspaces.OwnerUserId` → `AppUsers.Id` (`RESTRICT`)
3. **Partial unique indexes** (avoid duplicate active assets in one workspace):
   - `IX_ConnectedPages_WorkspaceId_PageId` `WHERE "IsConnected" = true`
   - `IX_ConnectedInstagramAccounts_WorkspaceId_IgBusinessId` `WHERE "IsConnected" = true`
4. **Missing lookup index**: `IX_MetaOAuthStates_WorkspaceId`.

## What data the migration could delete

Anything inserted **before the workspace feature existed**. The prior migration
`20260527203410_AddWorkspaceScopingAndCurrentWorkspaceId` added every `WorkspaceId`
column with `defaultValue = Guid.Empty` (`00000000-0000-0000-0000-000000000000`).
Rows that still carry that sentinel — and any other dangling FK — will be deleted
by the defensive `DELETE`s.

**Fresh database (dev/CI/QA):** safe — no data to lose, the `DELETE`s match zero rows.

**Production with existing data:** the only rows at risk are pre-workspace rows
nobody can currently access anyway (the workspace-scoped controllers already
hide them — they're stranded). Even so, **check first** with the script below.

## Step 1 — check for orphan rows

```bash
# Adjust for your environment
psql "$DATABASE_URL" -f scripts/check-workspace-orphans.sql
```

This script is **read-only**. It prints orphan counts per table and shows up to
10 sample rows so you can sanity-check what would be deleted.

- All counts `0` → migration is safe, proceed to step 4.
- Any count `> 0` → continue with step 2.

## Step 2 — back up the database

```bash
# Postgres dump (uncompressed plain SQL — easiest to diff/restore selectively)
pg_dump --no-owner --no-acl -Fc -f postpilot-prebackfill-$(date +%Y%m%d-%H%M).dump "$DATABASE_URL"
```

Keep this dump until you've verified the post-migration database. If anything
goes wrong, restore with `pg_restore -d "$DATABASE_URL" --clean <file>`.

## Step 3 — choose: backfill or delete

If the orphan counts are non-trivial, **do not let the migration delete them**.
Instead, backfill them into a real workspace before running the migration.

### 3a — backfill (preferred when orphan rows represent real work)

Decide which user / workspace should own the orphans. Most likely candidates:

- The original solo user, if you know who that is. Look at `AppUsers` and pick
  the oldest row.
- A new "Recovered (pre-workspaces)" workspace owned by that user.

```sql
-- One-shot pattern. Run inside a transaction; commit only when the numbers
-- match what you expected.
BEGIN;

-- Pick or create the destination workspace.
-- Replace <USER_ID> with the AppUsers.Id you want to own the recovered data.
INSERT INTO "Workspaces" ("Id", "Name", "OwnerUserId", "CreatedAt", "UpdatedAt")
VALUES (
  gen_random_uuid(),
  'Recovered (pre-workspaces)',
  '<USER_ID>',
  NOW(),
  NOW()
)
RETURNING "Id" AS recovered_workspace_id;
-- Note the Id printed; substitute it as <WS_ID> below.

-- Ensure the user is a member.
INSERT INTO "WorkspaceMembers" ("Id", "WorkspaceId", "UserId", "Role", "CreatedAt")
VALUES (gen_random_uuid(), '<WS_ID>', '<USER_ID>', 'Owner', NOW())
ON CONFLICT DO NOTHING;

-- Backfill each orphan column. Order matters: children first to make the
-- counts easier to audit row-by-row.
UPDATE "PostMediaItems" SET "WorkspaceId" = '<WS_ID>'
  WHERE "WorkspaceId" NOT IN (SELECT "Id" FROM "Workspaces");

UPDATE "Posts" SET "WorkspaceId" = '<WS_ID>'
  WHERE "WorkspaceId" NOT IN (SELECT "Id" FROM "Workspaces");

UPDATE "Media" SET "WorkspaceId" = '<WS_ID>'
  WHERE "WorkspaceId" NOT IN (SELECT "Id" FROM "Workspaces");

UPDATE "AiVoiceProfiles" SET "WorkspaceId" = '<WS_ID>'
  WHERE "WorkspaceId" NOT IN (SELECT "Id" FROM "Workspaces");

-- For MetaOAuthStates these are short-lived OAuth flows; safe to delete instead.
DELETE FROM "MetaOAuthStates"
  WHERE "WorkspaceId" NOT IN (SELECT "Id" FROM "Workspaces");

UPDATE "ConnectedPages" SET "WorkspaceId" = '<WS_ID>'
  WHERE "WorkspaceId" NOT IN (SELECT "Id" FROM "Workspaces");

UPDATE "ConnectedInstagramAccounts" SET "WorkspaceId" = '<WS_ID>'
  WHERE "WorkspaceId" NOT IN (SELECT "Id" FROM "Workspaces");

UPDATE "MetaConnections" SET "WorkspaceId" = '<WS_ID>'
  WHERE "WorkspaceId" NOT IN (SELECT "Id" FROM "Workspaces");

-- Re-run the diagnostic script in another session; confirm all counts are 0.

-- COMMIT only after verifying.
COMMIT;
```

Then re-run `scripts/check-workspace-orphans.sql` — it should report all zeros.

### 3b — accept the delete (only if orphan rows are genuinely junk)

If the orphans are clearly stale test data or partial backfill leftovers, you
can let the migration's built-in `DELETE`s clean them up. Just be sure the dump
from step 2 is intact, and re-run the diagnostic script once after the migration
to confirm.

## Step 4 — run the migration

```bash
cd backend
dotnet ef database update --connection "$DATABASE_URL"
```

EF Core wraps each migration in a transaction, so a mid-statement failure rolls
back. The migration is idempotent in the sense that re-running adds nothing new
once applied.

## Rollback plan

If the migration applies but you discover a problem afterwards:

```bash
# Roll back to the previous migration.
cd backend
dotnet ef database update 20260527203410_AddWorkspaceScopingAndCurrentWorkspaceId --connection "$DATABASE_URL"
```

This drops the new FKs and unique indexes. It does **not** restore rows the
defensive `DELETE`s removed — that's what the `pg_dump` from step 2 is for:

```bash
# Restore the pre-migration state (destructive).
pg_restore -d "$DATABASE_URL" --clean --if-exists postpilot-prebackfill-<timestamp>.dump
```

## What to verify after applying

```sql
-- Confirm FKs exist.
SELECT conname, pg_get_constraintdef(oid)
  FROM pg_constraint
 WHERE contype = 'f'
   AND conname LIKE '%Workspace%'
 ORDER BY conname;

-- Confirm partial unique indexes exist.
SELECT schemaname, tablename, indexname, indexdef
  FROM pg_indexes
 WHERE indexname IN (
   'IX_ConnectedPages_WorkspaceId_PageId',
   'IX_ConnectedInstagramAccounts_WorkspaceId_IgBusinessId',
   'IX_MetaOAuthStates_WorkspaceId'
 );

-- Confirm zero orphans remain.
\i scripts/check-workspace-orphans.sql
```

Run the workspace-isolation smoke tests from `docs/workspace-isolation.md` (or
the manual steps in the recent implementation report) once after migrating to a
real environment.
