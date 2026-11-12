using Microsoft.EntityFrameworkCore;

namespace CommandLineMcp.Data;

public sealed class CommandLineMcpDbContext(DbContextOptions<CommandLineMcpDbContext> options)
    : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Client> Clients => Set<Client>();
    public DbSet<Job> Jobs => Set<Job>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(entity =>
        {
            entity.ToTable("Users");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedOnAdd();
            entity.HasIndex(x => x.TelegramId).IsUnique();
            entity.Property(x => x.IsOAuthAllowed).HasDefaultValue(false);
            ConfigureAudit(entity);
        });

        modelBuilder.Entity<Client>(entity =>
        {
            entity.ToTable("Clients");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedOnAdd();
            entity.Property(x => x.Name).IsRequired();
            entity.Property(x => x.BearerKeyHash).IsRequired().HasMaxLength(64);
            entity.HasIndex(x => x.BearerKeyHash).IsUnique();
            entity.HasOne(x => x.User).WithMany(x => x.Clients).HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Restrict).IsRequired();
            ConfigureAudit(entity);
        });

        modelBuilder.Entity<Job>(entity =>
        {
            entity.ToTable("Jobs", table =>
            {
                table.HasCheckConstraint("CK_Jobs_TimeoutSeconds_Positive", "\"TimeoutSeconds\" > 0");
                table.HasCheckConstraint("CK_Jobs_Status", "\"Status\" IN ('Pending', 'Running', 'Succeeded', 'Failed', 'TimedOut', 'Unknown', 'Cancelled')");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedOnAdd();
            entity.Property(x => x.Status).HasConversion<string>().HasDefaultValue(JobStatus.Pending);
            entity.Property(x => x.Command).IsRequired();
            entity.HasOne(x => x.Client).WithMany(x => x.Jobs).HasForeignKey(x => x.ClientId)
                .OnDelete(DeleteBehavior.Restrict).IsRequired();
            entity.HasIndex(x => new { x.ClientId, x.Status, x.CreatedAt });
            ConfigureAudit(entity);
        });
    }

    private static void ConfigureAudit<TEntity>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<TEntity> entity)
        where TEntity : class, IAuditedEntity
    {
        entity.Property(x => x.CreatedAt)
            .IsRequired()
            .HasConversion(value => value, value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
        entity.Property(x => x.UpdatedAt)
            .IsRequired()
            .HasConversion(value => value, value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
    }

    public override int SaveChanges() => SaveChanges(true);

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ApplyAuditTimestamps();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        SaveChangesAsync(true, cancellationToken);

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        ApplyAuditTimestamps();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void ApplyAuditTimestamps()
    {
        var now = DateTime.UtcNow;
        foreach (var entry in ChangeTracker.Entries<IAuditedEntity>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAt = now;
                entry.Entity.UpdatedAt = now;
            }
            else if (entry.State == EntityState.Modified)
            {
                var createdAt = entry.Property(x => x.CreatedAt);
                entry.Entity.CreatedAt = createdAt.OriginalValue;
                createdAt.IsModified = false;
                entry.Entity.UpdatedAt = now;
            }
        }
    }
}
