using Backend.Constants;
using Backend.Database.Relational.Entities;
using Microsoft.EntityFrameworkCore;

namespace Backend.Relational.DAL;

public class WorkflowDbContext : DbContext
{
    public WorkflowDbContext(DbContextOptions<WorkflowDbContext> options)
        : base(options)
    {
        base.Database.Migrate();
    }

    public DbSet<ChannelSpecification> ChannelSchemas { get; set; }
    public DbSet<ChannelPropertyCriterion> ChannelParameterSchemas { get; set; }
    public DbSet<TaskSchema> TaskSchemas { get; set; }
    public DbSet<WorkflowBlueprint> WorkflowBlueprints { get; set; }
    public DbSet<WorkflowTask> WorkflowTasks { get; set; }
    public DbSet<WorkflowTaskChannel> WorkflowTaskChannels { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<ChannelSpecification>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Name)
                .IsRequired()
                .HasMaxLength(EntityMaxLengths.MaxChannelNameLength);

            entity.HasMany(e => e.Properties)
                .WithOne()
                .HasForeignKey(p => p.ChannelId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ChannelPropertyCriterion>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Name)
                .IsRequired()
                .HasMaxLength(EntityMaxLengths.MaxChannelParameterNameLength);

            entity.Property(e => e.Type)
                .IsRequired()
                .HasMaxLength(EntityMaxLengths.MaxChannelParameterTypeLength);
        });

        modelBuilder.Entity<TaskSchema>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Name)
                .IsRequired()
                .HasMaxLength(EntityMaxLengths.MaxTaskSchemaNameLength);

            entity.Property(e => e.Version)
                .HasConversion(
                    inValue => inValue.ToString(),
                    outValue => Version.Parse(outValue))
                .HasMaxLength(EntityMaxLengths.MaxTaskSchemaVersionLength);

            entity.HasOne(e => e.InputChannelSpecification)
                .WithOne()
                .HasForeignKey<TaskSchema>(e => e.InputChannelSpecificationId)
                .OnDelete(DeleteBehavior.NoAction);

            entity.HasIndex(t => t.InputChannelSpecificationId)
                .IsUnique();

            entity.HasMany(t => t.OutputChannelSpecifications)
                .WithOne(c => c.TaskSchema)
                .HasForeignKey(c => c.TaskSchemaId)
                .IsRequired(false)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WorkflowBlueprint>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Name)
                .IsRequired()
                .HasMaxLength(EntityMaxLengths.MaxWorkflowBlueprintNameLength);

            entity.Property(e => e.Description)
                .HasMaxLength(EntityMaxLengths.MaxWorkflowBlueprintDescriptionLength);

            entity.HasMany(e => e.Tasks)
                .WithOne(t => t.Blueprint)
                .HasForeignKey(t => t.BlueprintId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WorkflowTask>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.HasOne(e => e.Schema)
                .WithMany()
                .HasForeignKey(e => e.SchemaId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.Blueprint)
                .WithMany(wb => wb.Tasks)
                .HasForeignKey(e => e.BlueprintId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.InputChannel)
                .WithOne(c => c.DestinationNode)
                .HasForeignKey<WorkflowTaskChannel>(wtc => wtc.DestinationNodeId)
                .OnDelete(DeleteBehavior.NoAction);

            entity.HasMany(e => e.OutputChannels)
                .WithOne(c => c.SourceNode)
                .HasForeignKey(e => e.SourceNodeId)
                .OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<WorkflowTaskChannel>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.HasOne(e => e.Schema)
                .WithMany()
                .HasForeignKey(e => e.SchemaId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // Optional: Apply configurations via Fluent API
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(WorkflowDbContext).Assembly);
    }
}
