using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Sky.Referral.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;

namespace Coflnet.Sky.Referral.Services;

public class RewardProgramServiceTests
{
    private const string ClaimCode = "0123456789abcdef0123456789abcdef";
    private const string OfferVersion = "2026-08-08";

    [Test]
    public async Task AnonymousAwardCanBeClaimedWithoutChangingItsFinancialRecord()
    {
        await using var db = NewDb();
        var service = NewService(db);
        var result = await service.Append(new(
            "report:42", null, RewardLedgerEntryKind.Award, RewardSource.Report,
            1_000, null, null, OfferVersion, ClaimCode,
            "Approved report", null), "reviewer");

        var claimed = await service.Claim("reporter-1", ClaimCode);
        var balance = await service.GetBalance("reporter-1");

        Assert.Multiple(() =>
        {
            Assert.That(result.Created, Is.True);
            Assert.That(claimed.Id, Is.EqualTo(result.Entry.Id));
            Assert.That(claimed.WasAnonymous, Is.True);
            Assert.That(claimed.ClaimCodeHash, Is.Null);
            Assert.That(claimed.RemunerationEurCents, Is.EqualTo(1_000));
            Assert.That(balance.AvailableEurCents, Is.EqualTo(1_000));
        });
        Assert.ThrowsAsync<RewardProgramException>(() =>
            service.Claim("another-account", ClaimCode));
    }

    [Test]
    public async Task ReportAndReferralEuroRewardsShareThePayoutPolicy()
    {
        await using var db = NewDb();
        var service = NewService(db);
        await service.Append(new(
            "report:approved:1", "participant", RewardLedgerEntryKind.Award,
            RewardSource.Report, 1_200, null, null, "bug-bounty-v1", null,
            "Approved report reward", null), "reviewer");
        await service.Append(new(
            "referral:qualified:1", "participant", RewardLedgerEntryKind.Award,
            RewardSource.Referral, 800, null, null, "referral-v1", null,
            "Qualified referral reward", null), "referrals");

        var balance = await service.GetBalance("participant");

        Assert.Multiple(() =>
        {
            Assert.That(balance.AvailableEurCents, Is.EqualTo(2_000));
            Assert.That(balance.PayoutThresholdEurCents, Is.EqualTo(7_000));
        });
    }

    [Test]
    public async Task CreatorFeeMovesFromPendingToAvailableAndCanBeSettled()
    {
        await using var db = NewDb();
        var service = NewService(db);
        var pending = await service.Append(new(
            "config-acquisition:1", "creator-1", RewardLedgerEntryKind.Pending,
            RewardSource.CreatorFee, 7_000, null, null, "creator-root-hash", null,
            "Fixed Expert Content creator fee", "{\"configId\":\"starter\"}"), "modcommands");
        Assert.That((await service.GetBalance("creator-1")).PendingEurCents,
            Is.EqualTo(7_000));

        await service.Append(new(
            "config-available:1", "creator-1", RewardLedgerEntryKind.Award,
            RewardSource.CreatorFee, 7_000, null, pending.Entry.Id, null, null,
            "Payment final and content supplied", null), "modcommands");
        Assert.ThrowsAsync<RewardProgramException>(() => service.Append(new(
            "payout-request:too-high", "creator-1", RewardLedgerEntryKind.PayoutRequest,
            null, 7_001, null, null, null, null, "Invalid request", null),
            "finance"));
        Assert.ThrowsAsync<RewardProgramException>(() => service.Append(new(
            "payout-request:too-low", "creator-1", RewardLedgerEntryKind.PayoutRequest,
            null, 6_999, null, null, null, null, "Invalid request", null),
            "finance"));
        var payoutRequest = await service.Append(new(
            "payout-request:1", "creator-1", RewardLedgerEntryKind.PayoutRequest,
            null, 7_000, null, null, null, null, "Manual payout requested",
            PayoutDetails(7_000)),
            "finance");
        var reserved = await service.GetBalance("creator-1");
        await service.Append(new(
            "payout:1", "creator-1", RewardLedgerEntryKind.Payout, null, -7_000,
            null, payoutRequest.Entry.Id, null, null, "Paid after tax review",
            "{\"paymentReference\":\"bank:1\"}"), "finance");
        var paid = await service.GetBalance("creator-1");

        Assert.Multiple(() =>
        {
            Assert.That(reserved.PendingEurCents, Is.Zero);
            Assert.That(reserved.OutstandingEurCents, Is.EqualTo(7_000));
            Assert.That(reserved.ReservedEurCents, Is.EqualTo(7_000));
            Assert.That(reserved.AvailableEurCents, Is.Zero);
            Assert.That(paid.OutstandingEurCents, Is.Zero);
            Assert.That(paid.ReservedEurCents, Is.Zero);
        });
    }

    [Test]
    public async Task DefaultPayoutThresholdIsSeventyEurosAndIsEnforced()
    {
        await using var db = NewDb();
        var service = NewService(db);
        await service.Append(new(
            "report:threshold-default", "participant", RewardLedgerEntryKind.Award,
            RewardSource.Report, 6_999, null, null, OfferVersion, null,
            "Approved reward below the default threshold", null), "reviewer");

        Assert.That((await service.GetBalance("participant")).PayoutThresholdEurCents,
            Is.EqualTo(7_000));
        Assert.ThrowsAsync<RewardProgramException>(() => service.Append(new(
            "payout-request:below-default", "participant",
            RewardLedgerEntryKind.PayoutRequest, null, 6_999, null, null, null,
            null, "Below the default threshold", null), "finance"));
    }

    [Test]
    public async Task ProgrammeSpecificLowerThresholdIsInheritedAndValidatedOnAward()
    {
        // A reward programme can record a lower payout threshold directly on
        // its Pending entry (outside the public Append API, which never lets
        // a fresh Pending entry pick its own threshold). Awards settling that
        // Pending entry must inherit and validate against that lower value
        // rather than the account-wide default of 7000 (GetBalance then also
        // surfaces it - see BalanceThresholdIsTheMinimumOfDefaultAndAvailableAwardThresholds).
        await using var db = NewDb();
        var service = NewService(db);
        var lowThresholdPending = new RewardLedgerEntry
        {
            Id = Guid.NewGuid(),
            Reference = "creator-programme:legacy-pending",
            RewardAccountId = "programme-participant",
            Kind = RewardLedgerEntryKind.Pending,
            Source = RewardSource.CreatorFee,
            RemunerationEurCents = 1_000,
            PayoutThresholdEurCents = 1_000,
            OfferVersion = "legacy-programme-v1",
            WasAnonymous = false,
            Reason = "Legacy programme pending fee with a lower threshold",
            CreatedBy = "seed",
            CreatedAt = DateTime.UtcNow
        };
        db.RewardLedger.Add(lowThresholdPending);
        await db.SaveChangesAsync();

        Assert.ThrowsAsync<RewardProgramException>(() => service.Append(new(
            "creator-programme:legacy-award:wrong-threshold", "programme-participant",
            RewardLedgerEntryKind.Award, RewardSource.CreatorFee, 1_000, 7_000,
            lowThresholdPending.Id, "legacy-programme-v1", null,
            "Programme fee available", null), "modcommands"));
        Assert.DoesNotThrowAsync(() => service.Append(new(
            "creator-programme:legacy-award", "programme-participant",
            RewardLedgerEntryKind.Award, RewardSource.CreatorFee, 1_000, 1_000,
            lowThresholdPending.Id, "legacy-programme-v1", null,
            "Programme fee available", null), "modcommands"));
    }

    [Test]
    public async Task BalanceThresholdIsTheMinimumOfDefaultAndAvailableAwardThresholds()
    {
        await using var db = NewDb();
        var service = NewService(db);

        // An account whose awards only ever used the default threshold keeps
        // being refused below 7000.
        await service.Append(new(
            "award:default-threshold-only", "default-account",
            RewardLedgerEntryKind.Award, RewardSource.Report, 7_000, null, null,
            OfferVersion, null, "Approved reward", null), "reviewer");
        Assert.That((await service.GetBalance("default-account")).PayoutThresholdEurCents,
            Is.EqualTo(7_000));
        Assert.ThrowsAsync<RewardProgramException>(() => service.Append(new(
            "payout-request:default-account:below", "default-account",
            RewardLedgerEntryKind.PayoutRequest, null, 6_999, null, null, null,
            null, "Below the default threshold", null), "finance"));

        // An account whose only available award carries a programme
        // threshold of 2000 (recorded directly on the ledger, as a programme
        // would) can request a payout at that lower threshold.
        var lowThresholdPending = new RewardLedgerEntry
        {
            Id = Guid.NewGuid(),
            Reference = "creator-programme:balance-pending",
            RewardAccountId = "programme-account",
            Kind = RewardLedgerEntryKind.Pending,
            Source = RewardSource.CreatorFee,
            RemunerationEurCents = 2_000,
            PayoutThresholdEurCents = 2_000,
            OfferVersion = "legacy-programme-v1",
            WasAnonymous = false,
            Reason = "Legacy programme pending fee with a lower threshold",
            CreatedBy = "seed",
            CreatedAt = DateTime.UtcNow
        };
        db.RewardLedger.Add(lowThresholdPending);
        await db.SaveChangesAsync();
        await service.Append(new(
            "creator-programme:balance-award", "programme-account",
            RewardLedgerEntryKind.Award, RewardSource.CreatorFee, 2_000, 2_000,
            lowThresholdPending.Id, "legacy-programme-v1", null,
            "Programme fee available", null), "modcommands");

        Assert.That((await service.GetBalance("programme-account")).PayoutThresholdEurCents,
            Is.EqualTo(2_000));
        Assert.DoesNotThrowAsync(() => service.Append(new(
            "payout-request:programme-account", "programme-account",
            RewardLedgerEntryKind.PayoutRequest, null, 2_000, null, null, null,
            null, "Meets the programme's lower threshold", PayoutDetails(2_000)),
            "finance"));
    }

    [Test]
    public async Task PayoutFeeFieldMustBeZeroAndIsExcludedFromNet()
    {
        await using var db = NewDb();
        var service = NewService(db);
        await service.Append(new(
            "award:no-fee-field", "no-fee-account", RewardLedgerEntryKind.Award,
            RewardSource.Report, 7_000, null, null, OfferVersion, null,
            "Approved reward", null), "reviewer");
        await service.Append(new(
            "award:zero-fee-field", "zero-fee-account", RewardLedgerEntryKind.Award,
            RewardSource.Report, 7_000, null, null, OfferVersion, null,
            "Approved reward", null), "reviewer");
        await service.Append(new(
            "award:nonzero-fee-field", "bad-fee-account", RewardLedgerEntryKind.Award,
            RewardSource.Report, 7_000, null, null, OfferVersion, null,
            "Approved reward", null), "reviewer");

        Assert.DoesNotThrowAsync(() => service.Append(new(
            "payout-request:no-fee-field", "no-fee-account",
            RewardLedgerEntryKind.PayoutRequest, null, 7_000, null, null, null,
            null, "No fee field supplied", PayoutDetails(7_000)), "finance"));
        Assert.DoesNotThrowAsync(() => service.Append(new(
            "payout-request:zero-fee-field", "zero-fee-account",
            RewardLedgerEntryKind.PayoutRequest, null, 7_000, null, null, null,
            null, "Fee field explicitly zero",
            PayoutDetails(7_000, payoutFeeEurCents: 0)), "finance"));
        Assert.ThrowsAsync<RewardProgramException>(() => service.Append(new(
            "payout-request:nonzero-fee-field", "bad-fee-account",
            RewardLedgerEntryKind.PayoutRequest, null, 7_000, null, null, null,
            null, "Coflnet no longer charges a payout fee",
            PayoutDetails(7_000, payoutFeeEurCents: 400)), "finance"));
    }

    [TestCase(950, 0, 0, 7_950)]
    [TestCase(0, 750, 41, 6_209)]
    public async Task SettlementEvidenceAccountsForVatAndWithholding(
        long creatorVat,
        long withholding,
        long solidarity,
        long expectedNet)
    {
        await using var db = NewDb();
        var service = NewService(db);
        await service.Append(new(
            $"award:tax:{expectedNet}", "participant",
            RewardLedgerEntryKind.Award, RewardSource.CreatorFee, 7_000,
            null, null, OfferVersion, null, "Creator fee", null), "reviewer");

        Assert.DoesNotThrowAsync(() => service.Append(new(
            $"payout-request:tax:{expectedNet}", "participant",
            RewardLedgerEntryKind.PayoutRequest, null, 7_000, null, null,
            null, null, "Tax-aware payout",
            PayoutDetails(7_000, creatorVat, withholding,
                solidarity)), "finance"));
    }

    [Test]
    public async Task ReferenceIsIdempotentButCannotBeReusedForAnotherEvent()
    {
        await using var db = NewDb();
        var service = NewService(db);
        var request = new RewardEntryRequest(
            "report:retry", "reporter", RewardLedgerEntryKind.Award,
            RewardSource.Report, 500, null, null, OfferVersion, null,
            "Approved report", null);

        Assert.That((await service.Append(request, "reviewer")).Created, Is.True);
        Assert.That((await service.Append(request, "reviewer")).Created, Is.False);
        Assert.ThrowsAsync<RewardProgramException>(() => service.Append(
            request with { RemunerationEurCents = 501 }, "reviewer"));
    }

    [Test]
    public async Task LiabilityReportIncludesAvailableEntriesAndOpenPendingFees()
    {
        await using var db = NewDb();
        var service = NewService(db);
        await service.Append(new(
            "report:liability", "reporter", RewardLedgerEntryKind.Award,
            RewardSource.Report, 1_000, null, null, OfferVersion, null,
            "Approved report", null), "reviewer");
        await service.Append(new(
            "config:pending", "creator", RewardLedgerEntryKind.Pending,
            RewardSource.CreatorFee, 700, null, null, "creator-root", null,
            "Pending creator fee", null), "modcommands");

        var liability = await service.GetLiability(DateTime.UtcNow.AddSeconds(1));

        Assert.Multiple(() =>
        {
            Assert.That(liability.Currency, Is.EqualTo("EUR"));
            Assert.That(liability.OutstandingEurCents, Is.EqualTo(1_000));
            Assert.That(liability.PendingEurCents, Is.EqualTo(700));
            Assert.That(liability.TotalEntries, Is.EqualTo(2));
        });
    }

    [Test]
    public async Task RefundedCreatorFeeIsCancelledWhilePendingOrCorrectedAfterAward()
    {
        await using var db = NewDb();
        var service = NewService(db);
        var pending = await service.Append(new(
            "expert-config:41:pending", "creator", RewardLedgerEntryKind.Pending,
            RewardSource.CreatorFee, 700, null, null, OfferVersion, null,
            "Pending creator fee", null), "modcommands");
        var availablePending = await service.Append(new(
            "expert-config:42:pending", "creator", RewardLedgerEntryKind.Pending,
            RewardSource.CreatorFee, 900, null, null, OfferVersion, null,
            "Pending creator fee", null), "modcommands");
        await service.Append(new(
            "expert-config:42:available", "creator", RewardLedgerEntryKind.Award,
            RewardSource.CreatorFee, 900, null, availablePending.Entry.Id, null, null,
            "Content supplied", null), "modcommands");

        await service.ReverseCreatorFee(41);
        await service.ReverseCreatorFee(42);
        await service.ReverseCreatorFee(42);

        var ledger = await service.GetLedger("creator");
        var balance = await service.GetBalance("creator");
        Assert.Multiple(() =>
        {
            Assert.That(ledger.Single(entry => entry.Reference == "expert-config:41:refund").Kind,
                Is.EqualTo(RewardLedgerEntryKind.Cancellation));
            Assert.That(ledger.Single(entry => entry.Reference == "expert-config:42:refund").Kind,
                Is.EqualTo(RewardLedgerEntryKind.Correction));
            Assert.That(balance.AvailableEurCents, Is.Zero);
            Assert.That(balance.PendingEurCents, Is.Zero);
        });
    }

    [Test]
    public async Task RefundObservedBeforeCreatorFeeClosesTheLaterPendingEntry()
    {
        await using var db = NewDb();
        var service = NewService(db);

        await service.ReverseCreatorFee(43);
        await service.Append(new(
            "expert-config:43:pending", "creator", RewardLedgerEntryKind.Pending,
            RewardSource.CreatorFee, 700, null, null, OfferVersion, null,
            "Pending creator fee", null), "modcommands");

        var ledger = await service.GetLedger("creator");
        var balance = await service.GetBalance("creator");
        Assert.Multiple(() =>
        {
            Assert.That(ledger.Single(entry =>
                    entry.Reference == "expert-config:43:refund").Kind,
                Is.EqualTo(RewardLedgerEntryKind.Cancellation));
            Assert.That(balance.PendingEurCents, Is.Zero);
        });
    }

    [Test]
    public async Task FinancialLedgerFieldsCannotBeUpdatedOrDeleted()
    {
        await using var db = NewDb();
        var entry = (await NewService(db).Append(new(
            "report:immutable", "reporter", RewardLedgerEntryKind.Award,
            RewardSource.Report, 1_000, null, null, OfferVersion, null,
            "Approved report", null), "reviewer")).Entry;

        entry.RemunerationEurCents = 1;
        Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.Entry(entry).State = EntityState.Unchanged;
        db.Remove(entry);
        Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    private static ReferralDbContext NewDb() => new(
        new DbContextOptionsBuilder<ReferralDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static RewardProgramService NewService(ReferralDbContext db) => new(
        db,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["REWARDS:ENABLED"] = "true",
            ["REWARDS:WRITE_ACTOR"] = "tests"
        }).Build());

    private static string PayoutDetails(
        long gross,
        long creatorVat = 0,
        long withholding = 0,
        long solidarity = 0,
        long? payoutFeeEurCents = null) =>
        $"{{\"grossEurCents\":{gross},"
        + $"\"creatorVatEurCents\":{creatorVat},"
        + $"\"withholdingTaxEurCents\":{withholding},"
        + $"\"solidaritySurchargeEurCents\":{solidarity},"
        + (payoutFeeEurCents == null ? "" : $"\"payoutFeeEurCents\":{payoutFeeEurCents},")
        + $"\"netEurCents\":{gross + creatorVat - withholding - solidarity},"
        + "\"payoutEvidenceReference\":\"evidence:payout_test\","
        + $"\"payoutEvidenceSha256\":\"{new string('a', 64)}\"}}";
}
