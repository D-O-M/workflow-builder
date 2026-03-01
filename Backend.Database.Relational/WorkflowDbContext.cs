using Backend.Database.Relational.Entities;
using Microsoft.EntityFrameworkCore;

namespace Backend.Database.Relational;

public class WorkflowDbContext : DbContext
{
    public WorkflowDbContext(DbContextOptions<WorkflowDbContext> options)
        : base(options)
    {
        //base.Database.Migrate();
    }

    public DbSet<BlueprintDefinition> BlueprintDefinitions => Set<BlueprintDefinition>();
    public DbSet<BlueprintVersion> BlueprintVersions => Set<BlueprintVersion>();

    public DbSet<TaskDefinition> TaskDefinitions => Set<TaskDefinition>();
    public DbSet<TaskVersion> TaskVersions => Set<TaskVersion>();

    public DbSet<TaskInstance> TaskInstances => Set<TaskInstance>();
    public DbSet<TaskConnection> TaskConnections => Set<TaskConnection>();

    // TPH base DbSet is enough; derived are still queryable
    public DbSet<Channel> Channels => Set<Channel>();
    public DbSet<ChannelProperty> ChannelProperties => Set<ChannelProperty>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // -----------------------------
        // BlueprintDefinitions (BD)
        // -----------------------------
        modelBuilder.Entity<BlueprintDefinition>(entity =>
        {
            entity.ToTable("BlueprintDefinitions");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Key).IsRequired();
            entity.HasIndex(e => e.Key).IsUnique();

            entity.HasMany(e => e.Versions)
                  .WithOne(v => v.BlueprintDefinition)
                  .HasForeignKey(v => v.BlueprintDefinitionId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // -----------------------------
        // BlueprintVersions (BV)
        // -----------------------------
        modelBuilder.Entity<BlueprintVersion>(entity =>
        {
            entity.ToTable("BlueprintVersions");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Name).IsRequired();
            entity.Property(e => e.Version).IsRequired();

            entity.HasIndex(e => new { e.BlueprintDefinitionId, e.Version })
                  .IsUnique();

            // BV.StartingTaskInstanceId -> TI.Id
            // Restrict avoids cascade cycles (BV -> TI and TI -> BV)
            entity.HasOne(e => e.StartingTaskInstance)
                  .WithMany()
                  .HasForeignKey(e => e.StartingTaskInstanceId)
                  .OnDelete(DeleteBehavior.Restrict);

            // BV 1 -> many TI
            entity.HasMany(e => e.TaskInstances)
                  .WithOne(ti => ti.BlueprintVersion)
                  .HasForeignKey(ti => ti.BlueprintVersionId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // -----------------------------
        // TaskDefinitions (TD)
        // -----------------------------
        modelBuilder.Entity<TaskDefinition>(entity =>
        {
            entity.ToTable("TaskDefinitions");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Name).IsRequired();
            entity.HasIndex(e => e.Name).IsUnique();

            entity.HasMany(e => e.Versions)
                  .WithOne(v => v.TaskDefinition)
                  .HasForeignKey(v => v.TaskDefinitionId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // -----------------------------
        // TaskVersions (TV)
        // -----------------------------
        modelBuilder.Entity<TaskVersion>(entity =>
        {
            entity.ToTable("TaskVersions");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Path).IsRequired();
            entity.Property(e => e.Version)
                .HasConversion(
                    inValue => inValue.ToString(),
                    outValue => Version.Parse(outValue))
                .IsRequired();

            entity.HasIndex(e => new { e.TaskDefinitionId, e.Version })
                  .IsUnique();

            // TV 1 -> many TI
            // Restrict is typically safest if you don't want deleting a task version to delete runtime instances
            entity.HasMany(e => e.TaskInstances)
                  .WithOne(ti => ti.TaskVersion)
                  .HasForeignKey(ti => ti.TaskVersionId)
                  .OnDelete(DeleteBehavior.Restrict);

            // TV 1 -> many Channels (TPH)
            entity.HasMany(e => e.Channels)
                  .WithOne(c => c.TaskVersion)
                  .HasForeignKey(c => c.TaskVersionId)
                  .OnDelete(DeleteBehavior.NoAction);

        });

        // -----------------------------
        // TaskInstances (TI)
        // -----------------------------
        modelBuilder.Entity<TaskInstance>(entity =>
        {
            entity.ToTable("TaskInstances");
            entity.HasKey(e => e.Id);

            entity.HasMany(e => e.OutgoingConnections)
              .WithOne(tc => tc.SourceTaskInstance)
              .HasForeignKey(tc => tc.SourceTaskInstanceId)
              .OnDelete(DeleteBehavior.NoAction);

            entity.HasMany(e => e.IncomingConnections)
                  .WithOne(tc => tc.DestinationTaskInstance)
                  .HasForeignKey(tc => tc.DestinationTaskInstanceId)
                  .OnDelete(DeleteBehavior.NoAction);
        });

        // -----------------------------
        // TaskConnections (TC)
        // -----------------------------
        modelBuilder.Entity<TaskConnection>(entity =>
        {
            entity.ToTable("TaskConnections");
            entity.HasKey(e => e.Id);

            // Nullable FKs
            entity.Property(e => e.SourceTaskInstanceId).IsRequired(false);
            entity.Property(e => e.DestinationTaskInstanceId).IsRequired(false);

            // TC -> Channel (many-to-one)
            // Restrict avoids unintended deletes of connections if a channel is removed.
            entity.HasOne(e => e.Channel)
                  .WithMany()
                  .HasForeignKey(e => e.ChannelId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        // -----------------------------
        // Channels (C) - TPH via Discriminator
        // -----------------------------
        modelBuilder.Entity<Channel>(entity =>
        {
            entity.ToTable("Channels");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Name).IsRequired();

            // Channel 1 -> many ChannelProperties
            entity.HasMany(e => e.Properties)
                  .WithOne(cp => cp.Channel)
                  .HasForeignKey(cp => cp.ChannelId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<GlobalStateDefinition>(entity =>
        {
            entity.HasIndex(e => e.TaskVersionId)
                  .IsUnique()
                  .HasDatabaseName("UX_Channels_TaskVersionId_GlobalState")
                  .HasFilter("[Discriminator] = 'GlobalStateDefinition'");
        });

        // -----------------------------
        // ChannelProperties (CP)
        // -----------------------------
        modelBuilder.Entity<ChannelProperty>(entity =>
        {
            entity.ToTable("ChannelProperties");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Name).IsRequired();
            entity.Property(e => e.Type).IsRequired();
        });

        // Optional: Apply configurations via Fluent API
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(WorkflowDbContext).Assembly);
    }

}
