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
    public DbSet<PhasedBlueprintDefinition> PhasedBlueprintDefinitions => Set<PhasedBlueprintDefinition>();
    public DbSet<PhasedBlueprintVersion> PhasedBlueprintVersions => Set<PhasedBlueprintVersion>();
    public DbSet<PhasedBlueprintPhase> PhasedBlueprintPhases => Set<PhasedBlueprintPhase>();
    public DbSet<PhasedBlueprintTransition> PhasedBlueprintTransitions => Set<PhasedBlueprintTransition>();
    public DbSet<PhasedBlueprintHandoffMapping> PhasedBlueprintHandoffMappings => Set<PhasedBlueprintHandoffMapping>();
    public DbSet<PhasedBlueprintCorrelationMapping> PhasedBlueprintCorrelationMappings => Set<PhasedBlueprintCorrelationMapping>();

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
            entity.Property(e => e.AbiVersionMajor).IsRequired();
            entity.Property(e => e.AbiVersionMinor).IsRequired();
            entity.Property(e => e.AbiInvocationEnvelopeSchemaVersion).IsRequired();
            entity.Property(e => e.AbiResultSchemaVersion).IsRequired();
            entity.Property(e => e.AbiHydrationProtocolVersion).IsRequired();
            entity.Property(e => e.AbiProcessProtocolVersion).IsRequired();
            entity.Property(e => e.AbiHarnessVersion).IsRequired();
            entity.Property(e => e.AbiSupportedStateStoreModesJson).IsRequired();
            entity.Property(e => e.AbiSupportedPropertyKindsJson).IsRequired();
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
            entity.Property(e => e.NodeKind)
                .HasConversion<string>()
                .IsRequired();
            entity.Property(e => e.CollectionIteratorItemKind).IsRequired(false);
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
        // PhasedBlueprintDefinitions
        // -----------------------------
        modelBuilder.Entity<PhasedBlueprintDefinition>(entity =>
        {
            entity.ToTable("PhasedBlueprintDefinitions");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Key).IsRequired();
            entity.Property(e => e.DisplayName).IsRequired();
            entity.HasIndex(e => e.Key).IsUnique();

            entity.HasMany(e => e.Versions)
                .WithOne(e => e.PhasedBlueprintDefinition)
                .HasForeignKey(e => e.PhasedBlueprintDefinitionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PhasedBlueprintVersion>(entity =>
        {
            entity.ToTable("PhasedBlueprintVersions");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Version).IsRequired();
            entity.Property(e => e.Description).IsRequired(false);
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.HasIndex(e => new { e.PhasedBlueprintDefinitionId, e.Version }).IsUnique();

            entity.HasMany(e => e.Phases)
                .WithOne(e => e.PhasedBlueprintVersion)
                .HasForeignKey(e => e.PhasedBlueprintVersionId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.Transitions)
                .WithOne(e => e.PhasedBlueprintVersion)
                .HasForeignKey(e => e.PhasedBlueprintVersionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PhasedBlueprintPhase>(entity =>
        {
            entity.ToTable("PhasedBlueprintPhases");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.PhaseIndex).IsRequired();
            entity.Property(e => e.Name).IsRequired();
            entity.Property(e => e.VersionPolicy)
                .HasConversion<string>()
                .IsRequired();

            entity.HasIndex(e => new { e.PhasedBlueprintVersionId, e.PhaseIndex }).IsUnique();

            entity.HasOne(e => e.BlueprintDefinition)
                .WithMany()
                .HasForeignKey(e => e.BlueprintDefinitionId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.BlueprintVersion)
                .WithMany()
                .HasForeignKey(e => e.BlueprintVersionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PhasedBlueprintTransition>(entity =>
        {
            entity.ToTable("PhasedBlueprintTransitions");
            entity.HasKey(e => e.Id);

            entity.HasIndex(e => new { e.PhasedBlueprintVersionId, e.SourcePhaseId }).IsUnique();
            entity.HasIndex(e => new { e.PhasedBlueprintVersionId, e.DestinationPhaseId }).IsUnique();

            entity.HasOne(e => e.SourcePhase)
                .WithMany()
                .HasForeignKey(e => e.SourcePhaseId)
                .OnDelete(DeleteBehavior.NoAction);

            entity.HasOne(e => e.DestinationPhase)
                .WithMany()
                .HasForeignKey(e => e.DestinationPhaseId)
                .OnDelete(DeleteBehavior.NoAction);

            entity.HasMany(e => e.HandoffMappings)
                .WithOne(e => e.PhasedBlueprintTransition)
                .HasForeignKey(e => e.PhasedBlueprintTransitionId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.CorrelationMappings)
                .WithOne(e => e.PhasedBlueprintTransition)
                .HasForeignKey(e => e.PhasedBlueprintTransitionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PhasedBlueprintHandoffMapping>(entity =>
        {
            entity.ToTable("PhasedBlueprintHandoffMappings");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.SourceKind)
                .HasConversion<string>()
                .IsRequired();
            entity.Property(e => e.SourceChannelName).IsRequired(false);
            entity.Property(e => e.SourceProperty).IsRequired();
            entity.Property(e => e.BridgePosition).IsRequired();
            entity.Property(e => e.TargetBlueprintNodeId).IsRequired();
            entity.Property(e => e.TargetProperty).IsRequired();
        });

        modelBuilder.Entity<PhasedBlueprintCorrelationMapping>(entity =>
        {
            entity.ToTable("PhasedBlueprintCorrelationMappings");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.SourceResultProperty).IsRequired();
            entity.Property(e => e.TriggerProperty).IsRequired();
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
                  .HasValue<OutputChannelDefinition>("OutputChannelDefinition");

            // Channel 1 -> many ChannelProperties
            entity.HasMany(e => e.Properties)
                  .WithOne(cp => cp.Channel)
                  .HasForeignKey(cp => cp.ChannelId)
                  .OnDelete(DeleteBehavior.Cascade);
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
            entity.Property(e => e.IsCollection).IsRequired();
            entity.Property(e => e.IsRequired).IsRequired();
            entity.Property(e => e.EnumValuesJson).IsRequired(false);
        });

        // Optional: Apply configurations via Fluent API
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(BlueprintDbContext).Assembly);
    }

}
