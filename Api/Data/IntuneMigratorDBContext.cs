using Microsoft.EntityFrameworkCore;
using intuneMigratorApi.Models;
using System.IO;

namespace intuneMigratorApi.Data 
{

    
    public class IntuneMigratorDBContext : DbContext
    {

        public IntuneMigratorDBContext(DbContextOptions<IntuneMigratorDBContext> options)
            : base(options)
        {
            var connectionString = Database.GetConnectionString();
            if (!string.IsNullOrEmpty(connectionString))
            {
                var parts = connectionString.Split(';');
                foreach (var part in parts)
                {
                    var segments = part.Split('=', 2);
                    if (segments.Length == 2 && segments[0].Trim().Equals("Data Source", StringComparison.OrdinalIgnoreCase))
                    {
                        var path = segments[1].Trim();
                        var dir = Path.GetDirectoryName(path);
                        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        {
                            Directory.CreateDirectory(dir);
                        }
                        break;
                    }
                }
            }
            Database.EnsureCreated();
        }

        public DbSet<MigrationStatusModel> MigrationStatuses { get; set; }
        public DbSet<MigrationStatusEntity> MigrationStatusEntities { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<MigrationStatusModel>().HasKey(c => new { c.Id, c.Status });


            modelBuilder.Entity<MigrationStatusEntity>(entity =>
            {
                entity.HasKey(e => e.Id);

                entity.Property(e => e.Id)
                    .ValueGeneratedNever();

                entity.Property(e => e.Name)
                    .IsRequired()
                    .HasMaxLength(50);
            });

            modelBuilder.Entity<MigrationStatusEntity>().HasData(
                Enum.GetValues<MigrationStatus>()
                    .Select(e => new MigrationStatusEntity
                    {
                        Id = (int)e,
                        Name = e.ToString()
                    })
            );

            base.OnModelCreating(modelBuilder);
        }

    }

}