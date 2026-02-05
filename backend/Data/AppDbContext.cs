using Microsoft.EntityFrameworkCore;
using PostPilot.Api.Entities;

namespace PostPilot.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<Post> Posts => Set<Post>();
    public DbSet<MetaConnection> MetaConnections => Set<MetaConnection>();
    public DbSet<ConnectedPage> ConnectedPages => Set<ConnectedPage>();
    public DbSet<ConnectedInstagramAccount> ConnectedInstagramAccounts => Set<ConnectedInstagramAccount>();
    public DbSet<MetaOAuthState> MetaOAuthStates => Set<MetaOAuthState>();
    public DbSet<AiVoiceProfile> AiVoiceProfiles => Set<AiVoiceProfile>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Post>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Content).IsRequired();
            entity.Property(e => e.Platform).HasConversion<string>();
            entity.Property(e => e.Status).HasConversion<string>();

            // Index for finding due posts efficiently
            entity.HasIndex(e => new { e.Status, e.ScheduledAt });
            entity.HasIndex(e => new { e.Status, e.NextRetryAt });

            // Foreign key to ConnectedPage (optional - SET NULL on delete)
            entity.HasOne(e => e.TargetPage)
                .WithMany()
                .HasForeignKey(e => e.TargetPageId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<MetaConnection>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.UserId).IsUnique();
            entity.Property(e => e.AccessToken).IsRequired();

            entity.HasMany(e => e.Pages)
                .WithOne(p => p.MetaConnection)
                .HasForeignKey(p => p.MetaConnectionId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.InstagramAccounts)
                .WithOne(i => i.MetaConnection)
                .HasForeignKey(i => i.MetaConnectionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ConnectedPage>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.MetaConnectionId, e.PageId }).IsUnique();
            entity.Property(e => e.PageId).IsRequired();
            entity.Property(e => e.Name).IsRequired();
            entity.Property(e => e.AccessToken).IsRequired();
        });

        modelBuilder.Entity<ConnectedInstagramAccount>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.MetaConnectionId, e.IgBusinessId }).IsUnique();
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
            entity.HasIndex(e => e.UserId);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Description).HasMaxLength(1000);
            entity.Property(e => e.DoRules).HasMaxLength(2000);
            entity.Property(e => e.DontRules).HasMaxLength(2000);
            entity.Property(e => e.BannedWords).HasMaxLength(1000);
            entity.Property(e => e.ExamplePosts).HasMaxLength(5000);
        });

    }
}
