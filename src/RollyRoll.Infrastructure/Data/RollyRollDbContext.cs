using Microsoft.EntityFrameworkCore;
using RollyRoll.Core.Models;

namespace RollyRoll.Infrastructure.Data;

public class RollyRollDbContext : DbContext
{
    public RollyRollDbContext(DbContextOptions<RollyRollDbContext> options) : base(options) { }

    public DbSet<Client> Clients => Set<Client>();
    public DbSet<Image> Images => Set<Image>();
    public DbSet<DeploymentTemplate> DeploymentTemplates => Set<DeploymentTemplate>();
    public DbSet<ClientGroup> ClientGroups => Set<ClientGroup>();
    public DbSet<ScheduledTask> ScheduledTasks => Set<ScheduledTask>();
    public DbSet<PatchRolloutRing> PatchRolloutRings => Set<PatchRolloutRing>();
    public DbSet<PatchPackage> PatchPackages => Set<PatchPackage>();
    public DbSet<RecoverySnapshot> RecoverySnapshots => Set<RecoverySnapshot>();
    public DbSet<AuditLogEntry> AuditLogEntries => Set<AuditLogEntry>();
    public DbSet<ServerSetting> ServerSettings => Set<ServerSetting>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Client
        modelBuilder.Entity<Client>(entity =>
        {
            entity.HasIndex(e => e.MacAddress).IsUnique();
            entity.HasIndex(e => e.Hostname);
            entity.HasIndex(e => e.IpAddress);
            entity.Property(e => e.MacAddress).HasMaxLength(17).IsRequired();
            entity.Property(e => e.Hostname).HasMaxLength(255);
            entity.Property(e => e.IpAddress).HasMaxLength(45);
            entity.Property(e => e.OsVersion).HasMaxLength(100);
            entity.Property(e => e.HardwareModel).HasMaxLength(255);
            entity.Property(e => e.SerialNumber).HasMaxLength(100);
            entity.HasOne(e => e.Group)
                  .WithMany(g => g.Clients)
                  .HasForeignKey(e => e.GroupId)
                  .OnDelete(DeleteBehavior.SetNull);
        });

        // Image
        modelBuilder.Entity<Image>(entity =>
        {
            entity.HasIndex(e => e.Name);
            entity.Property(e => e.Name).HasMaxLength(255).IsRequired();
            entity.Property(e => e.FilePath).HasMaxLength(500).IsRequired();
            entity.Property(e => e.OsVersion).HasMaxLength(100);
            entity.Property(e => e.SourceClientHostname).HasMaxLength(255);
            entity.Property(e => e.SourceClientMac).HasMaxLength(17);
            entity.Property(e => e.FileHash).HasMaxLength(64);
        });

        // DeploymentTemplate
        modelBuilder.Entity<DeploymentTemplate>(entity =>
        {
            entity.HasIndex(e => e.Name).IsUnique();
            entity.Property(e => e.Name).HasMaxLength(255).IsRequired();
            entity.Property(e => e.DomainOuPath).HasMaxLength(500);
            entity.HasOne(e => e.Image)
                  .WithMany()
                  .HasForeignKey(e => e.ImageId)
                  .OnDelete(DeleteBehavior.SetNull);
        });

        // ClientGroup
        modelBuilder.Entity<ClientGroup>(entity =>
        {
            entity.HasIndex(e => e.Name).IsUnique();
            entity.Property(e => e.Name).HasMaxLength(255).IsRequired();
            entity.HasOne(e => e.DefaultTemplate)
                  .WithMany()
                  .HasForeignKey(e => e.DefaultTemplateId)
                  .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.DefaultImage)
                  .WithMany()
                  .HasForeignKey(e => e.DefaultImageId)
                  .OnDelete(DeleteBehavior.SetNull);
        });

        // ScheduledTask
        modelBuilder.Entity<ScheduledTask>(entity =>
        {
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.ScheduledAt);
            entity.Property(e => e.Name).HasMaxLength(255);
            entity.Property(e => e.CronExpression).HasMaxLength(100);
            entity.Property(e => e.CreatedBy).HasMaxLength(255);
            entity.HasOne(e => e.Client)
                  .WithMany(c => c.ScheduledTasks)
                  .HasForeignKey(e => e.ClientId)
                  .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Group)
                  .WithMany()
                  .HasForeignKey(e => e.GroupId)
                  .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.Image)
                  .WithMany()
                  .HasForeignKey(e => e.ImageId)
                  .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.Template)
                  .WithMany()
                  .HasForeignKey(e => e.TemplateId)
                  .OnDelete(DeleteBehavior.SetNull);
        });

        // PatchRolloutRing
        modelBuilder.Entity<PatchRolloutRing>(entity =>
        {
            entity.HasIndex(e => e.Order).IsUnique();
            entity.Property(e => e.Name).HasMaxLength(100).IsRequired();
            entity.HasMany(e => e.Groups)
                  .WithMany();
        });

        // PatchPackage
        modelBuilder.Entity<PatchPackage>(entity =>
        {
            entity.HasIndex(e => e.KbArticleId);
            entity.HasIndex(e => e.IsApproved);
            entity.Property(e => e.Title).HasMaxLength(500).IsRequired();
            entity.Property(e => e.KbArticleId).HasMaxLength(20);
            entity.Property(e => e.Version).HasMaxLength(50);
            entity.Property(e => e.ApprovedBy).HasMaxLength(255);
            entity.Property(e => e.InstallerPath).HasMaxLength(500);
        });

        // RecoverySnapshot
        modelBuilder.Entity<RecoverySnapshot>(entity =>
        {
            entity.HasIndex(e => e.ClientId);
            entity.HasIndex(e => e.CreatedAt);
            entity.Property(e => e.UserProfilesPath).HasMaxLength(500);
            entity.HasOne(e => e.Client)
                  .WithMany(c => c.RecoverySnapshots)
                  .HasForeignKey(e => e.ClientId)
                  .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Image)
                  .WithMany()
                  .HasForeignKey(e => e.ImageId)
                  .OnDelete(DeleteBehavior.SetNull);
        });

        // AuditLogEntry
        modelBuilder.Entity<AuditLogEntry>(entity =>
        {
            entity.HasIndex(e => e.Timestamp);
            entity.HasIndex(e => e.Category);
            entity.HasIndex(e => e.PerformedBy);
            entity.Property(e => e.Action).HasMaxLength(100).IsRequired();
            entity.Property(e => e.PerformedBy).HasMaxLength(255).IsRequired();
            entity.Property(e => e.TargetName).HasMaxLength(255);
            entity.Property(e => e.SourceIpAddress).HasMaxLength(45);
        });

        // ServerSetting
        modelBuilder.Entity<ServerSetting>(entity =>
        {
            entity.HasIndex(e => e.Key).IsUnique();
            entity.HasIndex(e => e.Category);
            entity.Property(e => e.Key).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Category).HasMaxLength(50).IsRequired();
            entity.Property(e => e.DisplayName).HasMaxLength(255).IsRequired();
        });
    }
}
