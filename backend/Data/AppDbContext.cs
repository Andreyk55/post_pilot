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

            entity.HasIndex(e => e.WorkspaceId);

            // Index for finding due posts efficiently
            entity.HasIndex(e => new { e.Status, e.ScheduledAt });
            entity.HasIndex(e => new { e.Status, e.NextRetryAt });

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
        });

        modelBuilder.Entity<MetaConnection>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.WorkspaceId);
            // (WorkspaceId, UserId) is unique only among currently-connected rows so a
            // workspace doesn't accumulate duplicate active connections for the same user.
            // Disconnected rows remain as history so re-connecting produces a new active row.
            entity.HasIndex(e => new { e.WorkspaceId, e.UserId })
                .IsUnique()
                .HasFilter("\"IsConnected\" = true");
            entity.Property(e => e.AccessToken).IsRequired();
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
            entity.Property(e => e.PageId).IsRequired();
            entity.Property(e => e.Name).IsRequired();
            entity.Property(e => e.AccessToken).IsRequired();
        });

        modelBuilder.Entity<ConnectedInstagramAccount>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.WorkspaceId);
            entity.HasIndex(e => new { e.MetaConnectionId, e.IgBusinessId })
                .IsUnique()
                .HasFilter("\"IsConnected\" = true");
            entity.Property(e => e.IgBusinessId).IsRequired();
            entity.Property(e => e.Username).IsRequired();
            entity.Property(e => e.PageId).IsRequired();
            entity.Property(e => e.PageName).IsRequired();
        });

        modelBuilder.Entity<MetaOAuthState>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.State).IsUnique();
            entity.Property(e => e.State).IsRequired();
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
        });

        modelBuilder.Entity<WorkspaceMember>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Role).HasConversion<string>().HasMaxLength(32);
            entity.HasIndex(e => new { e.WorkspaceId, e.UserId }).IsUnique();
            entity.HasIndex(e => e.UserId);
        });
    }
}
