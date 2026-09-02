using System;
using System.Threading.Tasks;
using Coflnet.Sky.Referral.Models;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace Coflnet.Sky.Referral.Services;

public class CreatorOnboardingServiceTests
{
    private const string CreatorId = "creator-1";
    private const string MinecraftUuid = "e7246661de77474f94627fabf9880f60";

    [Test]
    public async Task LatestApprovedAdultReviewEnablesPaidPublication()
    {
        await using var db = NewDb();
        var service = new CreatorOnboardingService(db);
        await service.Append(Request(), "legal-reviewer");

        var result = await service.GetEligibility(CreatorId, MinecraftUuid);

        Assert.Multiple(() =>
        {
            Assert.That(result.Eligible, Is.True);
            Assert.That(result.PaidPublicationReady, Is.True);
        });
    }

    [Test]
    public async Task LaterSuspensionImmediatelyMakesCreatorIneligible()
    {
        await using var db = NewDb();
        var service = new CreatorOnboardingService(db);
        var approved = await service.Append(Request(), "legal-reviewer");
        await service.Append(Request() with
        {
            ReviewId = Guid.NewGuid(),
            Status = CreatorOnboardingStatus.Suspended,
            PreviousReviewId = approved.Id,
            ValidUntilUtc = DateTime.UtcNow.AddDays(-1),
            Reason = "Review expired"
        }, "legal-reviewer");

        Assert.That((await service.GetEligibility(CreatorId, MinecraftUuid)).Eligible,
            Is.False);
    }

    [Test]
    public void MinorApprovalRequiresRepresentativeAcceptance()
    {
        using var db = NewDb();
        var service = new CreatorOnboardingService(db);
        var request = Request() with
        {
            CapacityStatus = CreatorCapacityStatus.Minor16PlusWithGuardian,
            TaxDocumentRoute = CreatorTaxDocumentRoute.NotApplicable,
            RepresentativeAccountId = "discord:42"
        };

        Assert.ThrowsAsync<RewardProgramException>(() =>
            service.Append(request, "legal-reviewer"));
        Assert.ThrowsAsync<RewardProgramException>(() => service.Append(
            request with
            {
                Status = CreatorOnboardingStatus.Pending,
                RepresentativeAgreementHash = new string('b', 64),
                RepresentativeAcceptedAtUtc = DateTime.UtcNow
            }, "legal-reviewer"));
    }

    [Test]
    public async Task GuardianAcceptanceEnablesPaidPublicationBeforePayoutVerification()
    {
        await using var db = NewDb();
        var service = new CreatorOnboardingService(db);
        var pending = await service.Append(Request() with
        {
            Status = CreatorOnboardingStatus.Pending,
            CapacityStatus = CreatorCapacityStatus.Minor16PlusWithGuardian,
            RepresentativeAccountId = "discord:42",
            TaxDocumentRoute = CreatorTaxDocumentRoute.Statement,
            VerificationReference = null
        }, "legal-reviewer");
        var hash = new string('b', 64);
        var accepted = await service.AcceptGuardian(
            pending.Id, new(hash, "en"), "discord:42");
        Assert.That((await service.AcceptGuardian(
            pending.Id, new(hash, "en"), "discord:42")).Id,
            Is.EqualTo(accepted.Id));
        await service.Append(Request() with
        {
            ReviewId = Guid.NewGuid(),
            CapacityStatus = accepted.CapacityStatus,
            RepresentativeAccountId = accepted.RepresentativeAccountId,
            RepresentativeAgreementHash = accepted.RepresentativeAgreementHash,
            RepresentativeAcceptedAtUtc = accepted.RepresentativeAcceptedAtUtc,
            TaxDocumentRoute = CreatorTaxDocumentRoute.Statement,
            VerificationReference = null,
            PreviousReviewId = accepted.Id
        }, "legal-reviewer");

        var result = await service.GetEligibility(CreatorId, MinecraftUuid, hash);

        Assert.Multiple(() =>
        {
            Assert.That(result.Eligible, Is.True);
            Assert.That(result.PaidPublicationReady, Is.True);
        });
        Assert.That((await service.GetEligibility(
            CreatorId, MinecraftUuid, new string('c', 64))).Eligible, Is.False);
    }

    [Test]
    public async Task FreePublicationSupportsCountriesOutsidePaidTerritories()
    {
        await using var db = NewDb();
        var service = new CreatorOnboardingService(db);

        foreach (var country in new[] { "BR", "TR", "CA" })
        {
            var creatorId = $"creator-{country}";
            var review = Request() with
            {
                ReviewId = Guid.NewGuid(),
                CreatorUserId = creatorId,
                ResidenceCountry = country,
                TaxResidenceCountry = country,
                CapacityJurisdiction = country,
                TaxDocumentRoute = CreatorTaxDocumentRoute.NotApplicable,
                VerificationReference = null
            };
            await service.Append(review, "legal-reviewer");
            var result = await service.GetEligibility(creatorId, MinecraftUuid);
            Assert.Multiple(() =>
            {
                Assert.That(result.Eligible, Is.True, country);
                Assert.That(result.PaidPublicationReady, Is.False, country);
            });
        }
    }

    [Test]
    public async Task GuardianApprovedBrazilianMinorCanPublishFreeConfigs()
    {
        await using var db = NewDb();
        var service = new CreatorOnboardingService(db);
        var pending = await service.Append(Request() with
        {
            Status = CreatorOnboardingStatus.Pending,
            ResidenceCountry = "BR",
            TaxResidenceCountry = "BR",
            CapacityJurisdiction = "BR",
            CapacityStatus = CreatorCapacityStatus.Minor16PlusWithGuardian,
            TaxDocumentRoute = CreatorTaxDocumentRoute.NotApplicable,
            VerificationReference = null,
            RepresentativeAccountId = "discord:42"
        }, "legal-reviewer");
        var hash = new string('b', 64);
        var accepted = await service.AcceptGuardian(
            pending.Id, new(hash, "en"), "discord:42");
        await service.Append(Request() with
        {
            ReviewId = Guid.NewGuid(),
            ResidenceCountry = accepted.ResidenceCountry,
            TaxResidenceCountry = accepted.TaxResidenceCountry,
            CapacityJurisdiction = accepted.CapacityJurisdiction,
            CapacityStatus = accepted.CapacityStatus,
            TaxDocumentRoute = accepted.TaxDocumentRoute,
            VerificationReference = null,
            RepresentativeAccountId = accepted.RepresentativeAccountId,
            RepresentativeAgreementHash = accepted.RepresentativeAgreementHash,
            RepresentativeAcceptedAtUtc = accepted.RepresentativeAcceptedAtUtc,
            PreviousReviewId = accepted.Id
        }, "legal-reviewer");

        var result = await service.GetEligibility(CreatorId, MinecraftUuid, hash);
        Assert.Multiple(() =>
        {
            Assert.That(result.Eligible, Is.True);
            Assert.That(result.PaidPublicationReady, Is.False);
        });
    }

    [Test]
    public void PaidPublicationRejectsCanadaScotlandAndNorthernIreland()
    {
        using var db = NewDb();
        var service = new CreatorOnboardingService(db);

        Assert.Multiple(() =>
        {
            Assert.ThrowsAsync<RewardProgramException>(() => service.Append(
                Request() with
                {
                    ResidenceCountry = "BR",
                    TaxResidenceCountry = "BR",
                    CapacityJurisdiction = "CA",
                    TaxDocumentRoute = CreatorTaxDocumentRoute.NotApplicable
                }, "legal-reviewer"));
            Assert.ThrowsAsync<RewardProgramException>(() => service.Append(
                Request() with
                {
                    ResidenceCountry = "CA",
                    TaxResidenceCountry = "CA",
                    CapacityJurisdiction = "CA",
                    TaxDocumentRoute = CreatorTaxDocumentRoute.Statement
                }, "legal-reviewer"));
            Assert.ThrowsAsync<RewardProgramException>(() => service.Append(
                Request() with
                {
                    ResidenceCountry = "GB",
                    TaxResidenceCountry = "GB",
                    CapacityJurisdiction = "GB-SCT",
                    TaxDocumentRoute = CreatorTaxDocumentRoute.Statement
                }, "legal-reviewer"));
            Assert.ThrowsAsync<RewardProgramException>(() => service.Append(
                Request() with
                {
                    ResidenceCountry = "GB",
                    TaxResidenceCountry = "GB",
                    CapacityJurisdiction = "GB-NIR",
                    TaxDocumentRoute = CreatorTaxDocumentRoute.Statement
                }, "legal-reviewer"));
        });
    }

    [Test]
    public async Task SwitzerlandSupportsPaidPublicationWithTheStatementRoute()
    {
        await using var db = NewDb();
        var service = new CreatorOnboardingService(db);
        await service.Append(Request() with
        {
            ResidenceCountry = "CH",
            TaxResidenceCountry = "CH",
            CapacityJurisdiction = "CH",
            TaxDocumentRoute = CreatorTaxDocumentRoute.Statement,
            VerificationReference = null
        }, "legal-reviewer");

        var result = await service.GetEligibility(CreatorId, MinecraftUuid);

        Assert.Multiple(() =>
        {
            Assert.That(result.Eligible, Is.True);
            Assert.That(result.PaidPublicationReady, Is.True);
        });
    }

    [Test]
    public void SwitzerlandRejectsTheBusinessTaxRouteForAnIndividual()
    {
        using var db = NewDb();
        var service = new CreatorOnboardingService(db);

        // A CH individual must use the Statement route, not the CreatorInvoice
        // route reserved for CH businesses; the mismatch is rejected outright,
        // so the review is never persisted and PaidPublicationReady can never
        // become true for it.
        Assert.ThrowsAsync<RewardProgramException>(() => service.Append(
            Request() with
            {
                ResidenceCountry = "CH",
                TaxResidenceCountry = "CH",
                CapacityJurisdiction = "CH",
                TaxDocumentRoute = CreatorTaxDocumentRoute.CreatorInvoice,
                VerificationReference = null
            }, "legal-reviewer"));
    }

    [Test]
    public async Task BusinessRequiresAdultSignatoryAndBusinessTaxRoute()
    {
        await using var db = NewDb();
        var service = new CreatorOnboardingService(db);
        var request = Request() with
        {
            SellerType = CreatorSellerType.Business,
            CapacityJurisdiction = "DE",
            TaxDocumentRoute = CreatorTaxDocumentRoute.CreatorInvoice,
            ValidUntilUtc = DateTime.UtcNow.AddMonths(11)
        };

        Assert.That((await service.Append(request, "legal-reviewer")).SellerType,
            Is.EqualTo(CreatorSellerType.Business));
        await using var ukDb = NewDb();
        var uk = request with
        {
            ReviewId = Guid.NewGuid(),
            ResidenceCountry = "GB",
            TaxResidenceCountry = "GB",
            CapacityJurisdiction = "GB-ENG",
            TaxDocumentRoute = CreatorTaxDocumentRoute.UkSelfBilling
        };
        Assert.That((await new CreatorOnboardingService(ukDb)
            .Append(uk, "legal-reviewer")).TaxDocumentRoute,
            Is.EqualTo(CreatorTaxDocumentRoute.UkSelfBilling));
        Assert.ThrowsAsync<RewardProgramException>(() => service.Append(
            request with
            {
                ReviewId = Guid.NewGuid(),
                CapacityStatus = CreatorCapacityStatus.Minor16PlusWithGuardian
            }, "legal-reviewer"));
    }

    [Test]
    public async Task ReviewsAreIdempotentAndAppendOnly()
    {
        await using var db = NewDb();
        var service = new CreatorOnboardingService(db);
        var request = Request();
        var first = await service.Append(request, "legal-reviewer");
        var retry = await service.Append(request, "legal-reviewer");

        Assert.That(retry.Id, Is.EqualTo(first.Id));
        first.Reason = "changed";
        Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.Entry(first).State = EntityState.Unchanged;
        db.Remove(first);
        Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    private static CreatorOnboardingReviewRequest Request() => new(
        Guid.NewGuid(),
        CreatorId,
        MinecraftUuid,
        CreatorOnboardingStatus.Approved,
        "de",
        "DE",
        CreatorSellerType.Individual,
        "DE",
        CreatorCapacityStatus.AdultDeclared,
        CreatorTaxDocumentRoute.Statement,
        "privacy-2026-08-31",
        "verification:123",
        null,
        null,
        null,
        "provider-report:123",
        new string('a', 64),
        "expert-review-2026-08",
        DateTime.UtcNow.AddYears(1),
        "Identity, capacity and tax profile reviewed",
        null);

    private static ReferralDbContext NewDb() => new(
        new DbContextOptionsBuilder<ReferralDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
}
