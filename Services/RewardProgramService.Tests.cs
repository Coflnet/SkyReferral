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
            1_000, 5_000, null, OfferVersion, ClaimCode,
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
    public async Task CreatorFeeMovesFromPendingToAvailableAndCanBeSettled()
    {
        await using var db = NewDb();
        var service = NewService(db);
        var pending = await service.Append(new(
            "config-acquisition:1", "creator-1", RewardLedgerEntryKind.Pending,
            RewardSource.CreatorFee, 700, 5_000, null, "creator-root-hash", null,
            "70% of net receipts", "{\"configId\":\"starter\"}"), "modcommands");
        Assert.That((await service.GetBalance("creator-1")).PendingEurCents,
            Is.EqualTo(700));

        await service.Append(new(
            "config-available:1", "creator-1", RewardLedgerEntryKind.Award,
            RewardSource.CreatorFee, 700, null, pending.Entry.Id, null, null,
            "Payment final and content supplied", null), "modcommands");
        Assert.ThrowsAsync<RewardProgramException>(() => service.Append(new(
            "payout-request:too-high", "creator-1", RewardLedgerEntryKind.PayoutRequest,
            null, 701, null, null, null, null, "Invalid request", null),
            "finance"));
        var payoutRequest = await service.Append(new(
            "payout-request:1", "creator-1", RewardLedgerEntryKind.PayoutRequest,
            null, 700, null, null, null, null, "Manual payout requested", null),
            "finance");
        var reserved = await service.GetBalance("creator-1");
        await service.Append(new(
            "payout:1", "creator-1", RewardLedgerEntryKind.Payout, null, -700,
            null, payoutRequest.Entry.Id, null, null, "Paid after tax review",
            "{\"paymentReference\":\"bank:1\"}"), "finance");
        var paid = await service.GetBalance("creator-1");

        Assert.Multiple(() =>
        {
            Assert.That(reserved.PendingEurCents, Is.Zero);
            Assert.That(reserved.OutstandingEurCents, Is.EqualTo(700));
            Assert.That(reserved.ReservedEurCents, Is.EqualTo(700));
            Assert.That(reserved.AvailableEurCents, Is.Zero);
            Assert.That(paid.OutstandingEurCents, Is.Zero);
            Assert.That(paid.ReservedEurCents, Is.Zero);
        });
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
            ["REWARDS:PAYOUT_THRESHOLD_EUR_CENTS"] = "5000",
            ["REWARDS:WRITE_ACTOR"] = "tests"
        }).Build());
}
