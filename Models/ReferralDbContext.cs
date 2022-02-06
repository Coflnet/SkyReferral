using Microsoft.EntityFrameworkCore;

namespace Coflnet.Sky.Referral.Models
{
    /// <summary>
    /// <see cref="DbContext"/> For flip tracking
    /// </summary>
    public class ReferralDbContext : DbContext
    {
        public DbSet<ReferralElement> Referrals { get; set; }

        /// <summary>
        /// Creates a new instance of <see cref="ReferralDbContext"/>
        /// </summary>
        /// <param name="options"></param>
        public ReferralDbContext(DbContextOptions<ReferralDbContext> options)
        : base(options)
        {
        }

        /// <summary>
        /// Configures additional relations and indexes
        /// </summary>
        /// <param name="modelBuilder"></param>
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<ReferralElement>(entity =>
            {
                entity.HasIndex(e => e.Invited).IsUnique();
                entity.HasIndex(e => e.Inviter);
            });
        }
    }
}