using Backend.Database.Relational.Entities;
using Microsoft.EntityFrameworkCore;

namespace Backend.Database.Relational;

public class BlueprintDbContext : DbContext
{
    public BlueprintDbContext(DbContextOptions<BlueprintDbContext> options)
        : base(options)
    {
        //base.Database.Migrate();
    }

    public DbSet<BlueprintDefinition> BlueprintDefinitions => Set<BlueprintDefinition>();
    public DbSet<BlueprintVersion> BlueprintVersions => Set<BlueprintVersion>();

    public DbSet<TaskDefinition> TaskDefinitions => Set<TaskDefinition>();
    public DbSet<TaskVersion> TaskVersions => Set<TaskVersion>();

    public DbSet<BlueprintNode> BlueprintNodes => Set<BlueprintNode>();
    public DbSet<BlueprintConnection> BlueprintConnections => Set<BlueprintConnection>();

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
            entity.Property(e => e.DisplayName).IsRequired();
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

            entity.Property(e => e.Version).IsRequired();

            entity.HasIndex(e => new { e.BlueprintDefinitionId, e.Version })
                  .IsUnique();

            // BV.StartingBlueprintNodeId -> TI.Id
            // Restrict avoids cascade cycles (BV -> TI and TI -> BV)
            entity.Property(e => e.StartingBlueprintNodeId).IsRequired(false);
            entity.HasOne(e => e.StartingBlueprintNode)
                  .WithMany()
                  .HasForeignKey(e => e.StartingBlueprintNodeId)
                  .OnDelete(DeleteBehavior.Restrict);

            // BV 1 -> many TI
            entity.HasMany(e => e.BlueprintNodes)
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
            entity.Property(e => e.DisplayName).IsRequired(false);
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
            entity.Property(e => e.State)
                .HasConversion<string>()
                .IsRequired();
            entity.Property(e => e.Version)
                .HasConversion(
                    inValue => inValue.ToString(),
                    outValue => Version.Parse(outValue))
                .IsRequired();

            entity.HasIndex(e => new { e.TaskDefinitionId, e.Version })
                  .IsUnique();

            // TV 1 -> many Channels (TPH)
            entity.HasMany(e => e.Channels)
                  .WithOne(c => c.TaskVersion)
                  .HasForeignKey(c => c.TaskVersionId)
                  .OnDelete(DeleteBehavior.NoAction);

        });

        // -----------------------------
        // BlueprintNodes (TI)
        // -----------------------------
        modelBuilder.Entity<BlueprintNode>(entity =>
        {
            entity.ToTable("BlueprintNodes");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.AllowedMajor).IsRequired();
            entity.Property(e => e.AllowedMinor).IsRequired();
            entity.Property(e => e.PositionX).IsRequired();
            entity.Property(e => e.PositionY).IsRequired();
            entity.Property(e => e.DesignTimeInputOverridesJson).IsRequired(false);

            entity.HasOne(e => e.TaskDefinition)
                .WithMany()
                .HasForeignKey(e => e.TaskDefinitionId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasMany(e => e.OutgoingConnections)
              .WithOne(tc => tc.SourceBlueprintNode)
              .HasForeignKey(tc => tc.SourceBlueprintNodeId)
              .OnDelete(DeleteBehavior.NoAction);

            entity.HasMany(e => e.IncomingConnections)
                  .WithOne(tc => tc.DestinationBlueprintNode)
                  .HasForeignKey(tc => tc.DestinationBlueprintNodeId)
                  .OnDelete(DeleteBehavior.NoAction);
        });

        // -----------------------------
        // BlueprintConnections (TC)
        // -----------------------------
        modelBuilder.Entity<BlueprintConnection>(entity =>
        {
            entity.ToTable("BlueprintConnections");
            entity.HasKey(e => e.Id);

            // Nullable FKs
            entity.Property(e => e.SourceBlueprintNodeId).IsRequired(false);
            entity.Property(e => e.DestinationBlueprintNodeId).IsRequired(false);
            entity.Property(e => e.Side).IsRequired();
            entity.Property(e => e.ChannelName).IsRequired();
            entity.Property(e => e.PropertyMappingsJson).IsRequired(false);
        });

        // -----------------------------
        // Channels (C) - TPH via Discriminator
        // -----------------------------
        modelBuilder.Entity<Channel>(entity =>
        {
            entity.ToTable("Channels");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Name).IsRequired();

            entity.HasDiscriminator<string>("Discriminator")
                  .HasValue<Channel>("Channel")
                  .HasValue<InputChannelDefinition>("InputChannelDefinition")
                  .HasValue<OutputChannelDefinition>("OutputChannelDefinition")
                  .HasValue<GlobalStateDefinition>("GlobalStateDefinition");

            // Channel 1 -> many ChannelProperties
            entity.HasMany(e => e.Properties)
                  .WithOne(cp => cp.Channel)
                  .HasForeignKey(cp => cp.ChannelId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<GlobalStateDefinition>(entity =>
        {
            var indexBuilder = entity.HasIndex(e => e.TaskVersionId)
                .HasDatabaseName("UX_Channels_TaskVersionId_GlobalState");

            if (Database.IsSqlServer())
            {
                indexBuilder
                    .IsUnique()
                    .HasFilter("[Discriminator] = 'GlobalStateDefinition'");
            }
        });

        // -----------------------------
        // ChannelProperties (CP)
        // -----------------------------
        modelBuilder.Entity<ChannelProperty>(entity =>
        {
            entity.ToTable("ChannelProperties");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Name).IsRequired();
            entity.Property(e => e.Kind).IsRequired();
            entity.Property(e => e.IsRequired).IsRequired();
            entity.Property(e => e.EnumValuesJson).IsRequired(false);
        });

        // Optional: Apply configurations via Fluent API
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(BlueprintDbContext).Assembly);
    }

}
