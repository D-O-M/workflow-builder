using Microsoft.EntityFrameworkCore;

namespace Backend.Relational.DAL;

public class WorkflowDbContext : DbContext
{
    public WorkflowDbContext(DbContextOptions<WorkflowDbContext> options)
        : base(options)
    {
        base.Database.Migrate();
    }

    //public DbSet<ChannelSchema> Channels { get; set; }
    public DbSet<ChannelSchema> ChannelSchemas { get; set; }
    public DbSet<ChannelParameterSchema> ChannelParameterSchemas { get; set; }
    public DbSet<TaskSchema> TaskSchemas { get; set; }
    //public DbSet<Workflow> Workflows { get; set; }
    //public DbSet<TaskSchema> WorkflowTasks { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<ChannelSchema>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
        });

        modelBuilder.Entity<ChannelParameterSchema>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Type).IsRequired().HasMaxLength(50);
            entity.HasOne(e => e.Channel)
                .WithMany(c => c.Parameters)
                .HasForeignKey(e => e.ChannelId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TaskSchema>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(100);

            entity.Property(e => e.Version)
                .HasConversion(
                    inValue => inValue.ToString(),
                    outValue => Version.Parse(outValue));
        });

        /*modelBuilder.Entity<Workflow>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Description).HasMaxLength(500);
            entity.HasMany(e => e.Tasks)
                .WithOne(t => t.Workflow);
        });*/

        // Optional: Apply configurations via Fluent API
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(WorkflowDbContext).Assembly);
    }
}
