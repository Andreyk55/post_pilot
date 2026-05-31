using Microsoft.EntityFrameworkCore;
using PostPilot.Api.Entities;

namespace PostPilot.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<Post> Posts => Set<Post>();
    public DbSet<PostMediaItem> PostMediaItems => Set<PostMediaItem>();
    public DbSet<MetaConnection> MetaConnections => Set<MetaConnection>();
    public DbSet<ConnectedPage> ConnectedPages => Set<ConnectedPage>();
    public DbSet<ConnectedInstagramAccount> ConnectedInstagramAccounts => Set<ConnectedInstagramAccount>();
    public DbSet<MetaOAuthState> MetaOAuthStates => Set<MetaOAuthState>();
    public DbSet<AiVoiceProfile> AiVoiceProfiles => Set<AiVoiceProfile>();
    public DbSet<Media> Media => Set<Media>();
    public DbSet<AppUser> AppUsers => Set<AppUser>();
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<WorkspaceMember> WorkspaceMembers => Set<WorkspaceMember>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Post>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Platform).HasConversion<string>();
            entity.Property(e => e.Status).HasConversion<string>();
            entity.Property(e => e.PostType).HasConversion<string>().HasDefaultValue(PostPilot.Api.Enums.PostType.Feed);
            // Cancellation metadata. Stored as int because it's an open enum we may
            // extend; Status is stored as string (legacy) but new enums default to int.
            entity.Property(e => e.CancellationReason).HasConversion<int>();
            entity.Property(e => e.CanceledBecauseProvider).HasConversion<int?>();

            entity.HasIndex(e => e.WorkspaceId);

            // Index for finding due posts efficiently
            entity.HasIndex(e => new { e.Status, e.ScheduledAt });
            entity.HasIndex(e => new { e.Status, e.NextRetryAt });

            // Workspace FK — RESTRICT. A workspace with any post in it can't be hard-deleted.
            // Posts must be removed (or soft-archived) before the workspace itself goes away.
            entity.HasOne<Workspace>()
                .WithMany()
                .HasForeignKey(e => e.WorkspaceId)
                .OnDelete(DeleteBehavior.Restrict);

            // Foreign key to ConnectedPage — RESTRICT. Asset rows are soft-deleted
            // (DisconnectedAt + IsConnected=false), never hard-deleted, so Posts never lose
            // their target reference. Restrict acts as a schema-level safety net should
            // anyone ever attempt a real DELETE.
            entity.HasOne(e => e.TargetPage)
                .WithMany()
                .HasForeignKey(e => e.TargetPageId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.TargetInstagramAccount)
                .WithMany()
                .HasForeignKey(e => e.TargetInstagramAccountId)
                .OnDelete(DeleteBehavior.Restrict);

            // Media items relationship
            entity.HasMany(e => e.MediaItems)
                .WithOne(m => m.Post)
                .HasForeignKey(m => m.PostId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PostMediaItem>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.PostId, e.Order });
            entity.HasIndex(e => e.WorkspaceId);
            entity.Property(e => e.MediaUrl).IsRequired();

            // Workspace FK — RESTRICT. The parent Post already has the same restriction;
            // we keep one here too so a denormalized WorkspaceId can never point at a
            // workspace that no longer exists.
            entity.HasOne<Workspace>()
                .WithMany()
                .HasForeignKey(e => e.WorkspaceId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<MetaConnection>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.WorkspaceId);
            // Product rule: at most ONE active connection per (workspace, provider).
            // Disconnected rows remain as history so re-connecting the same
            // provider account can resurface published/canceled posts.
            entity.HasIndex(e => new { e.WorkspaceId, e.Provider })
                .IsUnique()
                .HasFilter("\"IsConnected\" = true");
            // GENERIC OWNERSHIP RULE (cross-workspace): a provider account
            // (Provider + ExternalAccountId) may be OWNED by only ONE workspace at a
            // time. Ownership is held while IsConnected = true (covers both
            // Status=Active and Status=ReauthRequired). A real Disconnect sets
            // IsConnected=false and releases ownership so another workspace can connect.
            // The partial filter is on IsConnected (not Status) precisely because
            // ReauthRequired must keep blocking. ProviderAccountId NULLs are excluded
            // so legacy rows that never resolved an identity don't collide.
            entity.HasIndex(e => new { e.Provider, e.ProviderAccountId })
                .IsUnique()
                .HasFilter("\"IsConnected\" = true AND \"ProviderAccountId\" IS NOT NULL");
            // Helper index: lookup-by-stable-identity when reconnecting an
            // account ("did this workspace already have a row for Meta user X?").
            entity.HasIndex(e => new { e.WorkspaceId, e.Provider, e.ProviderAccountId });
            entity.Property(e => e.Provider).HasConversion<int>();
            entity.Property(e => e.Status).HasConversion<int>();
            entity.Property(e => e.AccessToken).IsRequired();

            // Workspace FK — RESTRICT.
            entity.HasOne<Workspace>()
                .WithMany()
                .HasForeignKey(e => e.WorkspaceId)
                .OnDelete(DeleteBehavior.Restrict);
            // NOTE: No HasDefaultValue / HasSentinel on IsConnected — both would make EF
            // treat the C# default `true` as "unset" and omit IsConnected from INSERT,
            // which in turn confuses change-tracking when adding new children via a tracked
            // parent's navigation collection (entities land in Modified instead of Added).
            // The DB-level default `true` is preserved by the original AddSoftDisconnect
            // migration as a safety net for any non-EF insert path.

            // Child FK relaxed: parent connection may be removed (legacy paths) without
            // taking the child asset rows with it. Soft-delete is the normal path.
            entity.HasMany(e => e.Pages)
                .WithOne(p => p.MetaConnection)
                .HasForeignKey(p => p.MetaConnectionId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasMany(e => e.InstagramAccounts)
                .WithOne(i => i.MetaConnection)
                .HasForeignKey(i => i.MetaConnectionId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<ConnectedPage>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.WorkspaceId);
            // Same (MetaConnectionId, PageId) can recur across disconnected/reconnected cycles;
            // uniqueness only applies to currently-connected rows.
            entity.HasIndex(e => new { e.MetaConnectionId, e.PageId })
                .IsUnique()
                .HasFilter("\"IsConnected\" = true");
            // GENERIC OWNERSHIP RULE (cross-workspace): a Facebook Page (the external
            // asset id) may be OWNED by only ONE workspace at a time. The previous
            // per-workspace-only index allowed the same page in two workspaces ("agency
            // use case"); the product rule now forbids that — a page connected in
            // workspace A blocks workspace B until A disconnects. Ownership is held while
            // IsConnected = true (Active OR ReauthRequired).
            entity.HasIndex(e => e.PageId)
                .IsUnique()
                .HasFilter("\"IsConnected\" = true")
                .HasDatabaseName("IX_ConnectedPages_PageId_Owned");
            entity.Property(e => e.PageId).IsRequired();
            entity.Property(e => e.Name).IsRequired();
            entity.Property(e => e.Status).HasConversion<int>();
            entity.Property(e => e.AccessToken).IsRequired();

            // Workspace FK — RESTRICT.
            entity.HasOne<Workspace>()
                .WithMany()
                .HasForeignKey(e => e.WorkspaceId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ConnectedInstagramAccount>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.WorkspaceId);
            entity.HasIndex(e => new { e.MetaConnectionId, e.IgBusinessId })
                .IsUnique()
                .HasFilter("\"IsConnected\" = true");
            // GENERIC OWNERSHIP RULE (cross-workspace): an Instagram account (external
            // asset id) may be OWNED by only ONE workspace at a time. Mirrors ConnectedPage.
            entity.HasIndex(e => e.IgBusinessId)
                .IsUnique()
                .HasFilter("\"IsConnected\" = true")
                .HasDatabaseName("IX_ConnectedInstagramAccounts_IgBusinessId_Owned");
            entity.Property(e => e.IgBusinessId).IsRequired();
            entity.Property(e => e.Status).HasConversion<int>();
            entity.Property(e => e.Username).IsRequired();
            entity.Property(e => e.PageId).IsRequired();
            entity.Property(e => e.PageName).IsRequired();

            // Workspace FK — RESTRICT.
            entity.HasOne<Workspace>()
                .WithMany()
                .HasForeignKey(e => e.WorkspaceId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<MetaOAuthState>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.State).IsUnique();
            entity.Property(e => e.State).IsRequired();

            // Workspace FK — RESTRICT.
            entity.HasOne<Workspace>()
                .WithMany()
                .HasForeignKey(e => e.WorkspaceId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AiVoiceProfile>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.WorkspaceId);
            entity.HasIndex(e => e.UserId);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Description).HasMaxLength(1000);
            entity.Property(e => e.DoRules).HasMaxLength(2000);
            entity.Property(e => e.DontRules).HasMaxLength(2000);
            entity.Property(e => e.BannedWords).HasMaxLength(1000);
            entity.Property(e => e.ExamplePosts).HasMaxLength(5000);

            // Workspace FK — RESTRICT.
            entity.HasOne<Workspace>()
                .WithMany()
                .HasForeignKey(e => e.WorkspaceId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Media>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.WorkspaceId);
            entity.HasIndex(e => e.StorageKey).IsUnique();
            entity.HasIndex(e => e.Status);
            entity.Property(e => e.Status).HasConversion<string>();
            entity.Property(e => e.StorageProvider).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Bucket).HasMaxLength(255);
            entity.Property(e => e.StorageKey).IsRequired().HasMaxLength(500);
            entity.Property(e => e.OriginalFileName).HasMaxLength(500);
            entity.Property(e => e.ContentType).HasMaxLength(100);

            // Workspace FK — RESTRICT.
            entity.HasOne<Workspace>()
                .WithMany()
                .HasForeignKey(e => e.WorkspaceId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AppUser>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Email).IsRequired().HasMaxLength(320);
            entity.Property(e => e.DisplayName).IsRequired().HasMaxLength(200);
            entity.Property(e => e.AuthProvider).IsRequired().HasMaxLength(50);
            entity.Property(e => e.ExternalAuthUserId).IsRequired().HasMaxLength(255);
            entity.Property(e => e.AvatarUrl).HasMaxLength(1024);
            // Unique provider identity — find-or-create on login keys off this pair.
            entity.HasIndex(e => new { e.AuthProvider, e.ExternalAuthUserId }).IsUnique();
            entity.HasIndex(e => e.Email);
            // No FK constraint on CurrentWorkspaceId — it's a soft pointer that the
            // CurrentWorkspaceProvider re-validates against WorkspaceMember on every request.
        });

        modelBuilder.Entity<Workspace>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.HasIndex(e => e.OwnerUserId);

            // Owner FK — RESTRICT. A user with any workspace they own can't be hard-deleted.
            // If we ever build user-deletion we'll need to transfer ownership first.
            entity.HasOne<AppUser>()
                .WithMany()
                .HasForeignKey(e => e.OwnerUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<WorkspaceMember>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Role).HasConversion<string>().HasMaxLength(32);
            entity.HasIndex(e => new { e.WorkspaceId, e.UserId }).IsUnique();
            entity.HasIndex(e => e.UserId);

            // Membership FKs — CASCADE. Removing a workspace or a user should also remove
            // their membership rows; there is no historical value in keeping orphans here
            // (unlike e.g. ConnectedPage, which keeps history for Posts).
            entity.HasOne<Workspace>()
                .WithMany()
                .HasForeignKey(e => e.WorkspaceId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne<AppUser>()
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
