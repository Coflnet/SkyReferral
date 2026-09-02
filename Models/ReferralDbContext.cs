using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Coflnet.Sky.Referral.Models
{
    /// <summary>
    /// <see cref="DbContext"/> For flip tracking
    /// </summary>
    public class ReferralDbContext : DbContext
    {
        public DbSet<ReferralElement> Referrals { get; set; }
        public DbSet<RewardLedgerEntry> RewardLedger { get; set; }
        public DbSet<CreatorOnboardingReview> CreatorOnboardingReviews { get; set; }

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

            modelBuilder.Entity<RewardLedgerEntry>(entity =>
            {
                entity.HasIndex(e => e.Reference).IsUnique();
                entity.HasIndex(e => e.ClaimCodeHash).IsUnique();
                entity.HasIndex(e => new { e.RewardAccountId, e.CreatedAt });
                entity.HasIndex(e => e.RelatedEntryId);
                entity.HasOne<RewardLedgerEntry>()
                    .WithMany()
                    .HasForeignKey(e => e.RelatedEntryId)
                    .OnDelete(DeleteBehavior.Restrict);
                entity.ToTable("RewardLedger", table =>
                {
                    table.HasCheckConstraint(
                        "CK_RewardLedger_Kind",
                        "`Kind` BETWEEN 1 AND 6");
                    table.HasCheckConstraint(
                        "CK_RewardLedger_Source",
                        "`Source` IS NULL OR `Source` BETWEEN 1 AND 4");
                    table.HasCheckConstraint(
                        "CK_RewardLedger_Claim",
                        "(NOT `WasAnonymous` AND `RewardAccountId` IS NOT NULL AND `ClaimCodeHash` IS NULL AND `ClaimedAt` IS NULL) " +
                        "OR (`WasAnonymous` AND ((`RewardAccountId` IS NULL AND `ClaimCodeHash` IS NOT NULL AND `ClaimedAt` IS NULL) " +
                        "OR (`RewardAccountId` IS NOT NULL AND `ClaimCodeHash` IS NULL AND `ClaimedAt` IS NOT NULL)))");
                });
            });

            modelBuilder.Entity<CreatorOnboardingReview>(entity =>
            {
                entity.HasIndex(e => new { e.CreatorUserId, e.ReviewedAtUtc });
                entity.HasIndex(e => new { e.CreatorUserId, e.PreviousReviewId })
                    .IsUnique();
                entity.ToTable("CreatorOnboardingReviews", table =>
                {
                    table.HasCheckConstraint(
                        "CK_CreatorOnboardingReviews_Status",
                        "`Status` BETWEEN 1 AND 4");
                    table.HasCheckConstraint(
                        "CK_CreatorOnboardingReviews_SellerType",
                        "`SellerType` BETWEEN 1 AND 2");
                    table.HasCheckConstraint(
                        "CK_CreatorOnboardingReviews_CapacityStatus",
                        "`CapacityStatus` BETWEEN 1 AND 3");
                    table.HasCheckConstraint(
                        "CK_CreatorOnboardingReviews_TaxDocumentRoute",
                        "`TaxDocumentRoute` BETWEEN 1 AND 5");
                });
            });
        }

        public override int SaveChanges(bool acceptAllChangesOnSuccess)
        {
            ProtectAppendOnlyRecords();
            return base.SaveChanges(acceptAllChangesOnSuccess);
        }

        public override Task<int> SaveChangesAsync(
            bool acceptAllChangesOnSuccess,
            CancellationToken cancellationToken = default)
        {
            ProtectAppendOnlyRecords();
            return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }

        private void ProtectAppendOnlyRecords()
        {
            foreach (var entry in ChangeTracker.Entries<RewardLedgerEntry>()
                .Where(item => item.State is EntityState.Modified or EntityState.Deleted))
            {
                if (entry.State == EntityState.Modified
                    && entry.OriginalValues.GetValue<bool>(nameof(RewardLedgerEntry.WasAnonymous))
                    && entry.OriginalValues.GetValue<string>(nameof(RewardLedgerEntry.RewardAccountId)) == null
                    && entry.OriginalValues.GetValue<string>(nameof(RewardLedgerEntry.ClaimCodeHash)) != null
                    && entry.Entity.RewardAccountId != null
                    && entry.Entity.ClaimCodeHash == null
                    && entry.Entity.ClaimedAt != null
                    && entry.Properties.Where(property => property.IsModified).All(property =>
                        property.Metadata.Name is nameof(RewardLedgerEntry.RewardAccountId)
                            or nameof(RewardLedgerEntry.ClaimCodeHash)
                            or nameof(RewardLedgerEntry.ClaimedAt)))
                    continue;
                throw new InvalidOperationException(
                    "Reward ledger entries are append-only except for one anonymous claim.");
            }
            if (ChangeTracker.Entries<CreatorOnboardingReview>()
                .Any(item => item.State is EntityState.Modified or EntityState.Deleted))
                throw new InvalidOperationException(
                    "Creator onboarding reviews are append-only.");
        }
    }
}
