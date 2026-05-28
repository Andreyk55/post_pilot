-- ============================================================================
-- Workspace-orphan diagnostic script
-- ----------------------------------------------------------------------------
-- Run BEFORE applying migration 20260528130716_AddWorkspaceForeignKeysAndUniques.
-- That migration adds FOREIGN KEY constraints from every *_WorkspaceId column to
-- Workspaces.Id, and from WorkspaceMembers.UserId / Workspaces.OwnerUserId to
-- AppUsers.Id. It also includes defensive DELETEs that remove any row whose
-- WorkspaceId / UserId / OwnerUserId does not resolve.
--
-- This script does NOT modify any data. It reports orphan counts so you can
-- decide whether to:
--   (a) let the migration delete them (safe on a fresh dev DB),
--   (b) backfill them into a real workspace before migrating (preferred when
--       the rows represent real data that predates the workspace feature),
--   (c) abort and investigate.
--
-- Usage:
--   psql "$DATABASE_URL" -f scripts/check-workspace-orphans.sql
--
-- Empty result means the migration is safe — there's nothing to clean up.
-- ============================================================================

\echo '=== Orphan rows (WorkspaceId not in Workspaces) ==='
SELECT 'Posts'                      AS table_name, COUNT(*) AS orphan_count
  FROM "Posts"                      WHERE "WorkspaceId" NOT IN (SELECT "Id" FROM "Workspaces")
UNION ALL
SELECT 'PostMediaItems',             COUNT(*) FROM "PostMediaItems"
  WHERE "WorkspaceId" NOT IN (SELECT "Id" FROM "Workspaces")
UNION ALL
SELECT 'Media',                      COUNT(*) FROM "Media"
  WHERE "WorkspaceId" NOT IN (SELECT "Id" FROM "Workspaces")
UNION ALL
SELECT 'AiVoiceProfiles',            COUNT(*) FROM "AiVoiceProfiles"
  WHERE "WorkspaceId" NOT IN (SELECT "Id" FROM "Workspaces")
UNION ALL
SELECT 'MetaOAuthStates',            COUNT(*) FROM "MetaOAuthStates"
  WHERE "WorkspaceId" NOT IN (SELECT "Id" FROM "Workspaces")
UNION ALL
SELECT 'MetaConnections',            COUNT(*) FROM "MetaConnections"
  WHERE "WorkspaceId" NOT IN (SELECT "Id" FROM "Workspaces")
UNION ALL
SELECT 'ConnectedPages',             COUNT(*) FROM "ConnectedPages"
  WHERE "WorkspaceId" NOT IN (SELECT "Id" FROM "Workspaces")
UNION ALL
SELECT 'ConnectedInstagramAccounts', COUNT(*) FROM "ConnectedInstagramAccounts"
  WHERE "WorkspaceId" NOT IN (SELECT "Id" FROM "Workspaces");

\echo ''
\echo '=== Membership orphans ==='
SELECT 'WorkspaceMembers (missing workspace)' AS scope, COUNT(*) AS orphan_count
  FROM "WorkspaceMembers"           WHERE "WorkspaceId" NOT IN (SELECT "Id" FROM "Workspaces")
UNION ALL
SELECT 'WorkspaceMembers (missing user)',      COUNT(*) FROM "WorkspaceMembers"
  WHERE "UserId" NOT IN (SELECT "Id" FROM "AppUsers")
UNION ALL
SELECT 'Workspaces (missing owner)',           COUNT(*) FROM "Workspaces"
  WHERE "OwnerUserId" NOT IN (SELECT "Id" FROM "AppUsers");

\echo ''
\echo '=== Sample orphan rows (first 10 per table, for forensics) ==='
\echo '--- Posts ---'
SELECT "Id", "WorkspaceId", "Content", "Status", "CreatedAt"
  FROM "Posts"
 WHERE "WorkspaceId" NOT IN (SELECT "Id" FROM "Workspaces")
 LIMIT 10;

\echo '--- Media ---'
SELECT "Id", "WorkspaceId", "StorageKey", "Status", "CreatedAt"
  FROM "Media"
 WHERE "WorkspaceId" NOT IN (SELECT "Id" FROM "Workspaces")
 LIMIT 10;

\echo '--- MetaConnections ---'
SELECT "Id", "WorkspaceId", "UserId", "IsConnected", "ConnectedAt"
  FROM "MetaConnections"
 WHERE "WorkspaceId" NOT IN (SELECT "Id" FROM "Workspaces")
 LIMIT 10;

\echo ''
\echo '=== Pre-workspace-feature rows (WorkspaceId = all-zero Guid) ==='
\echo 'These are the rows the prior migration backfilled with Guid.Empty before'
\echo 'workspaces existed. If counts are > 0 on prod, decide between backfill or delete.'
SELECT 'Posts'                      AS table_name, COUNT(*) AS zero_count
  FROM "Posts"                      WHERE "WorkspaceId" = '00000000-0000-0000-0000-000000000000'
UNION ALL
SELECT 'Media',                      COUNT(*) FROM "Media"
  WHERE "WorkspaceId" = '00000000-0000-0000-0000-000000000000'
UNION ALL
SELECT 'AiVoiceProfiles',            COUNT(*) FROM "AiVoiceProfiles"
  WHERE "WorkspaceId" = '00000000-0000-0000-0000-000000000000'
UNION ALL
SELECT 'MetaConnections',            COUNT(*) FROM "MetaConnections"
  WHERE "WorkspaceId" = '00000000-0000-0000-0000-000000000000'
UNION ALL
SELECT 'ConnectedPages',             COUNT(*) FROM "ConnectedPages"
  WHERE "WorkspaceId" = '00000000-0000-0000-0000-000000000000'
UNION ALL
SELECT 'ConnectedInstagramAccounts', COUNT(*) FROM "ConnectedInstagramAccounts"
  WHERE "WorkspaceId" = '00000000-0000-0000-0000-000000000000';
