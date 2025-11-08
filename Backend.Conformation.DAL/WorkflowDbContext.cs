using Microsoft.EntityFrameworkCore;

namespace Backend.Conformation.DAL;

public class WorkflowDbContext : DbContext
{
    public WorkflowDbContext(DbContextOptions<WorkflowDbContext> options)
        : base(options)
    {
        base.Database.Migrate();
    }

    public DbSet<Channel> Channels { get; set; }
    public DbSet<ChannelParameter> ChannelParameters { get; set; }
    public DbSet<Workflow> Workflows { get; set; }
    public DbSet<WorkflowTask> WorkflowTasks { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Channel>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
        });

        modelBuilder.Entity<ChannelParameter>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Type).IsRequired().HasMaxLength(50);
            entity.HasOne(e => e.Channel)
                .WithMany(c => c.Parameters)
                .HasForeignKey(e => e.ChannelId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WorkflowTask>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
            entity.HasMany(entity => entity.InputChannels)
                .WithOne(channel => channel.DestinationNode)
                .HasForeignKey(channel => channel.DestinationNodeId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasMany(entity => entity.OutputChannels)
                .WithOne(channel => channel.SourceNode)
                .HasForeignKey(channel => channel.SourceNodeId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Workflow>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Description).HasMaxLength(500);
            entity.HasMany(e => e.Tasks)
                .WithMany()
                .UsingEntity<TaskWorkflow>("WorkflowToWorkflowTask");
            entity.HasMany(e => e.Channels)
                .WithMany()
                .UsingEntity<ChannelWorkflow>("WorkflowToChannel");
        });

        // Optional: Apply configurations via Fluent API
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(WorkflowDbContext).Assembly);
    }
}
