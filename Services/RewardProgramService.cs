using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Coflnet.Sky.Referral.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Coflnet.Sky.Referral.Services;

public class RewardProgramException(string message)
    : Coflnet.Sky.Core.CoflnetException("reward", message);

public record RewardEntryRequest(
    string Reference,
    string RewardAccountId,
    RewardLedgerEntryKind Kind,
    RewardSource? Source,
    long RemunerationEurCents,
    long? PayoutThresholdEurCents,
    Guid? RelatedEntryId,
    string OfferVersion,
    string ClaimCode,
    string Reason,
    string DetailsJson);

public record RewardEntryResult(RewardLedgerEntry Entry, bool Created);

public record RewardBalance(
    string RewardAccountId,
    string Currency,
    long PendingEurCents,
    long OutstandingEurCents,
    long ReservedEurCents,
    long AvailableEurCents,
    long PayoutThresholdEurCents);

public record RewardLiability(
    string Currency,
    DateTime ToExclusive,
    long OutstandingEurCents,
    long PendingEurCents,
    int TotalEntries,
    int Skip,
    bool HasMore,
    IReadOnlyList<RewardLedgerEntry> Entries);

public class RewardProgramService
{
    private const string ExpertConfigReference = "expert-config:";
    private const long PayoutThresholdEurCents = 7_000;
    private readonly ReferralDbContext db;
    private readonly IConfiguration configuration;

    public RewardProgramService(ReferralDbContext db, IConfiguration configuration)
    {
        this.db = db;
        this.configuration = configuration;
    }

    public async Task CheckReady()
    {
        EnsureEnabled();
        await db.RewardLedger.AsNoTracking().Select(entry => entry.Id)
            .FirstOrDefaultAsync();
    }

    public async Task<RewardEntryResult> Append(RewardEntryRequest request, string actor)
    {
        EnsureEnabled();
        if (request == null)
            throw new RewardProgramException("Request body is required");

        var reference = Required(request.Reference, "reference", 200);
        var accountId = Optional(request.RewardAccountId, 128);
        var reason = Required(request.Reason, "reason", 300);
        actor = Required(actor, "writer actor", 128);
        var details = ValidateDetails(request.DetailsJson);
        await using var ledgerLock = await DatabaseAdvisoryLock.Acquire(
            db, LockResource(request, reference, accountId));
        var related = request.RelatedEntryId == null
            ? null
            : await db.RewardLedger.AsNoTracking().SingleOrDefaultAsync(entry =>
                entry.Id == request.RelatedEntryId.Value)
                ?? throw new RewardProgramException("Related ledger entry was not found");
        var source = request.Source ?? related?.Source;
        var offerVersion = Optional(request.OfferVersion, 128) ?? related?.OfferVersion;
        long? threshold = request.Kind is RewardLedgerEntryKind.Pending
            or RewardLedgerEntryKind.Award
                ? related?.PayoutThresholdEurCents
                    ?? PayoutThresholdEurCents
                : null;
        if (request.PayoutThresholdEurCents != null
            && request.PayoutThresholdEurCents != threshold)
            throw new RewardProgramException(
                "The payout threshold is fixed by the programme");
        var claimCodeHash = accountId == null
            ? HashClaimCode(request.ClaimCode)
            : null;

        ValidateEntry(request, accountId, source, offerVersion, threshold, related, claimCodeHash);

        var entry = new RewardLedgerEntry
        {
            Id = Guid.NewGuid(),
            Reference = reference,
            RewardAccountId = accountId,
            Kind = request.Kind,
            Source = source,
            RemunerationEurCents = request.RemunerationEurCents,
            PayoutThresholdEurCents = threshold,
            RelatedEntryId = request.RelatedEntryId,
            OfferVersion = offerVersion,
            ClaimCodeHash = claimCodeHash,
            WasAnonymous = accountId == null,
            Reason = reason,
            CreatedBy = actor,
            DetailsJson = details,
            CreatedAt = DateTime.UtcNow
        };

        var existing = await db.RewardLedger.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Reference == reference);
        if (existing != null)
            return Match(existing, entry);
        ValidatePayoutEvidence(
            request.Kind, request.RemunerationEurCents, details);
        if (request.Kind == RewardLedgerEntryKind.PayoutRequest)
        {
            var balance = await GetBalance(accountId);
            if (request.RemunerationEurCents > balance.AvailableEurCents)
                throw new RewardProgramException("Payout request exceeds the available balance");
            if (request.RemunerationEurCents < balance.PayoutThresholdEurCents)
                throw new RewardProgramException("Payout request is below the payout threshold");
        }
        if (related != null && request.Kind != RewardLedgerEntryKind.Correction
            && await db.RewardLedger.AnyAsync(item => item.RelatedEntryId == related.Id
                && (item.Kind == RewardLedgerEntryKind.Award
                    || item.Kind == RewardLedgerEntryKind.Payout
                    || item.Kind == RewardLedgerEntryKind.Cancellation)))
            throw new RewardProgramException("Related ledger entry is already closed");

        db.RewardLedger.Add(entry);
        if (request.Kind == RewardLedgerEntryKind.Pending
            && request.Source == RewardSource.CreatorFee
            && TryExpertPurchaseId(reference, out var purchaseId)
            && await db.RewardLedger.AnyAsync(item =>
                item.Reference == $"{ExpertConfigReference}{purchaseId}:refund-seen"))
            db.RewardLedger.Add(new RewardLedgerEntry
            {
                Id = Guid.NewGuid(),
                Reference = $"{ExpertConfigReference}{purchaseId}:refund",
                RewardAccountId = entry.RewardAccountId,
                Kind = RewardLedgerEntryKind.Cancellation,
                Source = RewardSource.CreatorFee,
                RemunerationEurCents = 0,
                RelatedEntryId = entry.Id,
                WasAnonymous = false,
                Reason = "Expert Config purchase reverted before the creator fee was recorded",
                CreatedBy = actor,
                CreatedAt = DateTime.UtcNow
            });
        try
        {
            await db.SaveChangesAsync();
            return new(entry, true);
        }
        catch (DbUpdateException)
        {
            db.Entry(entry).State = EntityState.Detached;
            existing = await db.RewardLedger.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Reference == reference);
            if (existing == null)
                throw;
            return Match(existing, entry);
        }
    }

    public async Task ReverseCreatorFee(long purchaseTransactionId)
    {
        EnsureEnabled();
        await using var ledgerLock = await DatabaseAdvisoryLock.Acquire(
            db, $"expert-config:{purchaseTransactionId}");
        var pending = await db.RewardLedger.AsNoTracking().SingleOrDefaultAsync(entry =>
            entry.Reference == $"{ExpertConfigReference}{purchaseTransactionId}:pending"
            && entry.Kind == RewardLedgerEntryKind.Pending
            && entry.Source == RewardSource.CreatorFee);
        if (pending == null)
        {
            var reference = $"{ExpertConfigReference}{purchaseTransactionId}:refund-seen";
            if (await db.RewardLedger.AnyAsync(entry => entry.Reference == reference))
                return;
            db.RewardLedger.Add(new RewardLedgerEntry
            {
                Id = Guid.NewGuid(),
                Reference = reference,
                RewardAccountId = $"reverted-purchase:{purchaseTransactionId}",
                Kind = RewardLedgerEntryKind.Cancellation,
                Source = RewardSource.CreatorFee,
                RemunerationEurCents = 0,
                WasAnonymous = false,
                Reason = "Expert Config purchase reversion observed before creator fee creation",
                CreatedBy = Required(
                    configuration["REWARDS:WRITE_ACTOR"], "writer actor", 128),
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
            return;
        }

        var award = await db.RewardLedger.AsNoTracking().SingleOrDefaultAsync(entry =>
            entry.RelatedEntryId == pending.Id
            && entry.Kind == RewardLedgerEntryKind.Award);
        var actor = configuration["REWARDS:WRITE_ACTOR"];
        if (award == null)
        {
            await Append(new(
                $"{ExpertConfigReference}{purchaseTransactionId}:refund",
                pending.RewardAccountId,
                RewardLedgerEntryKind.Cancellation,
                null,
                0,
                null,
                pending.Id,
                null,
                null,
                "Expert Config purchase reverted before the creator fee became available",
                null), actor);
            return;
        }

        await Append(new(
            $"{ExpertConfigReference}{purchaseTransactionId}:refund",
            award.RewardAccountId,
            RewardLedgerEntryKind.Correction,
            null,
            -award.RemunerationEurCents,
            null,
            award.Id,
            null,
            null,
            "Expert Config purchase reverted after the creator fee became available",
            null), actor);
    }

    public async Task<RewardLedgerEntry> Claim(string rewardAccountId, string claimCode)
    {
        EnsureEnabled();
        rewardAccountId = Required(rewardAccountId, "reward account", 128);
        var hash = HashClaimCode(claimCode);
        var entry = await db.RewardLedger.SingleOrDefaultAsync(item =>
            item.ClaimCodeHash == hash && item.RewardAccountId == null);
        if (entry == null)
            throw new RewardProgramException("Claim code is invalid or already used");

        entry.RewardAccountId = rewardAccountId;
        entry.ClaimCodeHash = null;
        entry.ClaimedAt = DateTime.UtcNow;
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new RewardProgramException("Claim code is invalid or already used");
        }
        return entry;
    }

    public async Task<IReadOnlyList<RewardLedgerEntry>> GetLedger(string rewardAccountId)
    {
        EnsureEnabled();
        rewardAccountId = Required(rewardAccountId, "reward account", 128);
        return await db.RewardLedger.AsNoTracking()
            .Where(entry => entry.RewardAccountId == rewardAccountId)
            .OrderBy(entry => entry.CreatedAt)
            .ThenBy(entry => entry.Id)
            .ToListAsync();
    }

    public async Task<RewardBalance> GetBalance(string rewardAccountId)
    {
        var entries = await GetLedger(rewardAccountId);
        var closed = entries.Where(entry => entry.RelatedEntryId != null)
            .GroupBy(entry => entry.RelatedEntryId.Value)
            .ToDictionary(group => group.Key, group => group.Select(entry => entry.Kind).ToHashSet());
        var pending = entries.Where(entry => entry.Kind == RewardLedgerEntryKind.Pending
                && !IsClosed(entry.Id, RewardLedgerEntryKind.Award, RewardLedgerEntryKind.Cancellation))
            .Sum(entry => entry.RemunerationEurCents);
        var outstanding = entries.Where(IsBalanceEntry).Sum(entry => entry.RemunerationEurCents);
        var reserved = entries.Where(entry => entry.Kind == RewardLedgerEntryKind.PayoutRequest
                && !IsClosed(entry.Id, RewardLedgerEntryKind.Payout, RewardLedgerEntryKind.Cancellation))
            .Sum(entry => entry.RemunerationEurCents);
        // A reward programme can record a lower threshold on its Award
        // entries; the account's effective threshold is the lowest of the
        // default and every available award's own threshold.
        var threshold = entries.Where(entry => entry.Kind == RewardLedgerEntryKind.Award)
            .Select(entry => entry.PayoutThresholdEurCents ?? PayoutThresholdEurCents)
            .Append(PayoutThresholdEurCents)
            .Min();
        return new(rewardAccountId, "EUR", pending, outstanding, reserved,
            outstanding - reserved, threshold);

        bool IsClosed(Guid id, params RewardLedgerEntryKind[] kinds) =>
            closed.TryGetValue(id, out var children) && kinds.Any(children.Contains);
    }

    public async Task<RewardLiability> GetLiability(
        DateTime toExclusive,
        DateTime? fromInclusive = null,
        int skip = 0,
        int take = 500)
    {
        EnsureEnabled();
        if (toExclusive.Kind != DateTimeKind.Utc)
            throw new RewardProgramException("toExclusive must be UTC");
        if (fromInclusive is { Kind: not DateTimeKind.Utc })
            throw new RewardProgramException("fromInclusive must be UTC");
        if (skip < 0 || take is < 1 or > 500)
            throw new RewardProgramException("Invalid liability page");

        var asOf = db.RewardLedger.AsNoTracking()
            .Where(entry => entry.CreatedAt < toExclusive);
        var outstanding = await asOf.Where(IsBalanceEntryExpression())
            .SumAsync(entry => (long?)entry.RemunerationEurCents) ?? 0;
        var pendingEntries = await asOf
            .Where(entry => entry.Kind == RewardLedgerEntryKind.Pending)
            .Select(entry => new { entry.Id, entry.RemunerationEurCents })
            .ToListAsync();
        var closedPendingIds = await asOf.Where(entry =>
                entry.RelatedEntryId != null
                && (entry.Kind == RewardLedgerEntryKind.Award
                    || entry.Kind == RewardLedgerEntryKind.Cancellation))
            .Select(entry => entry.RelatedEntryId.Value)
            .ToListAsync();
        var pending = pendingEntries
            .Where(entry => !closedPendingIds.Contains(entry.Id))
            .Sum(entry => entry.RemunerationEurCents);
        var selected = fromInclusive == null
            ? asOf
            : asOf.Where(entry => entry.CreatedAt >= fromInclusive.Value);
        var total = await selected.CountAsync();
        var entries = await selected.OrderBy(entry => entry.CreatedAt)
            .ThenBy(entry => entry.Id)
            .Skip(skip)
            .Take(take)
            .ToListAsync();
        return new("EUR", toExclusive, outstanding, pending, total, skip,
            skip + entries.Count < total, entries);
    }

    internal static string HashClaimCode(string claimCode)
    {
        claimCode = Required(claimCode, "claim code", 256);
        if (claimCode.Length < 32)
            throw new RewardProgramException("Claim code must contain at least 32 characters");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(claimCode)))
            .ToLowerInvariant();
    }

    private static void ValidateEntry(
        RewardEntryRequest request,
        string accountId,
        RewardSource? source,
        string offerVersion,
        long? threshold,
        RewardLedgerEntry related,
        string claimCodeHash)
    {
        switch (request.Kind)
        {
            case RewardLedgerEntryKind.Pending:
                RequireAwardShape(accountId, source, offerVersion, threshold, request.RemunerationEurCents);
                if (related != null || claimCodeHash != null || !string.IsNullOrWhiteSpace(request.ClaimCode))
                    throw new RewardProgramException("A pending entry cannot be related or anonymous");
                break;
            case RewardLedgerEntryKind.Award:
                RequireAwardShape(accountId, source, offerVersion, threshold, request.RemunerationEurCents,
                    allowAnonymous: true, claimCodeHash);
                if (accountId != null && !string.IsNullOrWhiteSpace(request.ClaimCode))
                    throw new RewardProgramException("An identified award cannot have a claim code");
                if (related != null && (related.Kind != RewardLedgerEntryKind.Pending
                    || related.RewardAccountId != accountId
                    || related.Source != source
                    || related.RemunerationEurCents != request.RemunerationEurCents
                    || related.OfferVersion != offerVersion))
                    throw new RewardProgramException("Available award does not match its pending entry");
                break;
            case RewardLedgerEntryKind.Correction:
                RequireRelated(accountId, source, related, RewardLedgerEntryKind.Award);
                if (request.RemunerationEurCents == 0 || threshold != null || claimCodeHash != null)
                    throw new RewardProgramException("Invalid correction entry");
                break;
            case RewardLedgerEntryKind.PayoutRequest:
                if (accountId == null || request.RemunerationEurCents <= 0 || source != null
                    || related != null || threshold != null || claimCodeHash != null)
                    throw new RewardProgramException("Invalid payout request entry");
                break;
            case RewardLedgerEntryKind.Payout:
                RequireRelated(accountId, null, related, RewardLedgerEntryKind.PayoutRequest);
                if (request.RemunerationEurCents != -related.RemunerationEurCents
                    || source != null || threshold != null || claimCodeHash != null)
                    throw new RewardProgramException("Payout does not match its request");
                break;
            case RewardLedgerEntryKind.Cancellation:
                if (accountId == null || related?.RewardAccountId != accountId
                    || related.Kind is not (RewardLedgerEntryKind.Pending or RewardLedgerEntryKind.PayoutRequest)
                    || request.RemunerationEurCents != 0 || threshold != null || claimCodeHash != null)
                    throw new RewardProgramException("Invalid cancellation entry");
                break;
            default:
                throw new RewardProgramException("Unknown ledger entry kind");
        }
    }

    private static void RequireAwardShape(
        string accountId,
        RewardSource? source,
        string offerVersion,
        long? threshold,
        long amount,
        bool allowAnonymous = false,
        string claimCodeHash = null)
    {
        if (source == null || amount <= 0 || threshold <= 0 || string.IsNullOrEmpty(offerVersion)
            || accountId == null && (!allowAnonymous || claimCodeHash == null))
            throw new RewardProgramException("Invalid award entry");
    }

    private static void RequireRelated(
        string accountId,
        RewardSource? source,
        RewardLedgerEntry related,
        RewardLedgerEntryKind kind)
    {
        if (accountId == null || related?.Kind != kind || related.RewardAccountId != accountId
            || source != related.Source)
            throw new RewardProgramException("Related ledger entry does not match");
    }

    private static RewardEntryResult Match(RewardLedgerEntry existing, RewardLedgerEntry requested)
    {
        if (existing.RewardAccountId != requested.RewardAccountId
            || existing.Kind != requested.Kind
            || existing.Source != requested.Source
            || existing.RemunerationEurCents != requested.RemunerationEurCents
            || existing.PayoutThresholdEurCents != requested.PayoutThresholdEurCents
            || existing.RelatedEntryId != requested.RelatedEntryId
            || existing.OfferVersion != requested.OfferVersion
            || existing.ClaimCodeHash != requested.ClaimCodeHash
            || existing.Reason != requested.Reason
            || existing.DetailsJson != requested.DetailsJson)
            throw new RewardProgramException("Reference already identifies another ledger event");
        return new(existing, false);
    }

    private static bool IsBalanceEntry(RewardLedgerEntry entry) =>
        entry.Kind is RewardLedgerEntryKind.Award
            or RewardLedgerEntryKind.Correction
            or RewardLedgerEntryKind.Payout;

    private static string LockResource(
        RewardEntryRequest request,
        string reference,
        string accountId)
    {
        if (TryExpertPurchaseId(reference, out var purchaseId))
            return $"expert-config:{purchaseId}";
        if (request.Kind == RewardLedgerEntryKind.PayoutRequest)
            return $"payout:{accountId}";
        if (request.RelatedEntryId.HasValue
            && request.Kind != RewardLedgerEntryKind.Correction)
            return $"ledger-entry:{request.RelatedEntryId.Value:N}";
        return $"reference:{reference}";
    }

    private static bool TryExpertPurchaseId(string reference, out long id)
    {
        id = 0;
        if (!reference.StartsWith(ExpertConfigReference, StringComparison.Ordinal))
            return false;
        var end = reference.IndexOf(':', ExpertConfigReference.Length);
        return end > ExpertConfigReference.Length
            && long.TryParse(
                reference[ExpertConfigReference.Length..end], out id);
    }

    private static System.Linq.Expressions.Expression<Func<RewardLedgerEntry, bool>>
        IsBalanceEntryExpression() => entry =>
            entry.Kind == RewardLedgerEntryKind.Award
            || entry.Kind == RewardLedgerEntryKind.Correction
            || entry.Kind == RewardLedgerEntryKind.Payout;

    private void ValidatePayoutEvidence(
        RewardLedgerEntryKind kind,
        long amount,
        string details)
    {
        if (kind is not (RewardLedgerEntryKind.PayoutRequest
            or RewardLedgerEntryKind.Payout))
            return;
        if (details == null)
            throw new RewardProgramException("Payout evidence is required");

        using var document = JsonDocument.Parse(details);
        var root = document.RootElement;
        if (kind == RewardLedgerEntryKind.Payout)
        {
            RequireNamespacedDetail(root, "paymentReference");
            return;
        }

        var creatorVat = RequiredAmount(root, "creatorVatEurCents");
        var withholding = RequiredAmount(root, "withholdingTaxEurCents");
        var solidarity = RequiredAmount(root, "solidaritySurchargeEurCents");
        if (root.TryGetProperty("payoutFeeEurCents", out var feeProperty)
            && (!feeProperty.TryGetInt64(out var fee) || fee != 0))
            throw new RewardProgramException(
                "Coflnet no longer charges a payout fee; payoutFeeEurCents must be 0");
        if (RequiredAmount(root, "grossEurCents") != amount
            || RequiredAmount(root, "netEurCents")
                != amount + creatorVat - withholding - solidarity
            || amount + creatorVat - withholding - solidarity <= 0)
            throw new RewardProgramException("Payout gross and net are invalid");
        RequireNamespacedDetail(root, "payoutEvidenceReference");
        var evidenceHash = RequiredDetail(root, "payoutEvidenceSha256");
        if (evidenceHash.Length != 64
            || evidenceHash.Any(character => !Uri.IsHexDigit(character)))
            throw new RewardProgramException(
                "payoutEvidenceSha256 must be a SHA-256 digest");
    }

    private static long RequiredAmount(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var property)
            || !property.TryGetInt64(out var value) || value < 0)
            throw new RewardProgramException($"{name} is invalid");
        return value;
    }

    private static void RequireNamespacedDetail(JsonElement root, string name)
    {
        var value = RequiredDetail(root, name);
        var separator = value.IndexOf(':');
        if (separator < 1 || separator == value.Length - 1)
            throw new RewardProgramException($"{name} must be a namespaced reference");
    }

    private static string RequiredDetail(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(property.GetString()))
            throw new RewardProgramException($"{name} is required");
        return property.GetString().Trim();
    }

    private static string ValidateDetails(string value)
    {
        value = Optional(value, 4000);
        if (value == null)
            return null;
        try
        {
            using var document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException();
        }
        catch (JsonException)
        {
            throw new RewardProgramException("detailsJson must be a JSON object");
        }
        return value;
    }

    private static string Required(string value, string name, int maxLength) =>
        Optional(value, maxLength) ?? throw new RewardProgramException($"{name} is required");

    private static string Optional(string value, int maxLength)
    {
        value = value?.Trim();
        if (value?.Length > maxLength)
            throw new RewardProgramException($"Value may contain at most {maxLength} characters");
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private void EnsureEnabled()
    {
        if (!configuration.GetValue<bool>("REWARDS:ENABLED"))
            throw new RewardProgramException("Reward ledger is disabled");
    }
}
