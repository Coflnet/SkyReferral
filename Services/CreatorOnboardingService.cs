using System;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Sky.Referral.Models;
using Microsoft.EntityFrameworkCore;

namespace Coflnet.Sky.Referral.Services;

public record CreatorOnboardingReviewRequest(
    Guid ReviewId,
    string CreatorUserId,
    string MinecraftUuid,
    CreatorOnboardingStatus Status,
    string ResidenceCountry,
    string TaxResidenceCountry,
    CreatorSellerType SellerType,
    string CapacityJurisdiction,
    CreatorCapacityStatus CapacityStatus,
    CreatorTaxDocumentRoute TaxDocumentRoute,
    string PrivacyNoticeVersion,
    string VerificationReference,
    string RepresentativeAccountId,
    string RepresentativeAgreementHash,
    DateTime? RepresentativeAcceptedAtUtc,
    string EvidenceReference,
    string EvidenceSha256,
    string RuleVersion,
    DateTime? ValidUntilUtc,
    string Reason,
    Guid? PreviousReviewId);

public record CreatorEligibility(
    bool Eligible,
    bool PaidPublicationReady);

public record GuardianAcceptanceRequest(
    string AgreementHash,
    string Locale);

public class CreatorOnboardingService
{
    private static readonly string[] EuCountries =
    [
        "AT", "BE", "BG", "HR", "CY", "CZ", "DK", "EE", "FI", "FR",
        "DE", "GR", "HU", "IE", "IT", "LV", "LT", "LU", "MT", "NL",
        "PL", "PT", "RO", "SK", "SI", "ES", "SE"
    ];
    private readonly ReferralDbContext db;

    public CreatorOnboardingService(ReferralDbContext db)
    {
        this.db = db;
    }

    public async Task<CreatorOnboardingReview> Append(
        CreatorOnboardingReviewRequest request,
        string reviewer,
        bool guardianAcceptance = false)
    {
        if (request == null)
            throw new RewardProgramException("Request body is required");
        if (request.ReviewId == Guid.Empty)
            throw new RewardProgramException("reviewId is required");

        var now = DateTime.UtcNow;
        var review = new CreatorOnboardingReview
        {
            Id = request.ReviewId,
            CreatorUserId = Required(request.CreatorUserId, "creatorUserId", 128),
            MinecraftUuid = MinecraftUuid(request.MinecraftUuid),
            Status = request.Status,
            ResidenceCountry = Country(request.ResidenceCountry, "residenceCountry"),
            TaxResidenceCountry = Country(request.TaxResidenceCountry, "taxResidenceCountry"),
            SellerType = request.SellerType,
            CapacityJurisdiction = Jurisdiction(
                request.CapacityJurisdiction, "capacityJurisdiction"),
            CapacityStatus = request.CapacityStatus,
            TaxDocumentRoute = request.TaxDocumentRoute,
            PrivacyNoticeVersion = Required(
                request.PrivacyNoticeVersion, "privacyNoticeVersion", 64),
            VerificationReference = VerificationReference(
                request.VerificationReference),
            RepresentativeAccountId = Optional(
                request.RepresentativeAccountId, "representativeAccountId", 32),
            RepresentativeAgreementHash = OptionalSha256(
                request.RepresentativeAgreementHash,
                "representativeAgreementHash"),
            RepresentativeAcceptedAtUtc = Utc(
                request.RepresentativeAcceptedAtUtc,
                "representativeAcceptedAtUtc"),
            EvidenceReference = Required(
                request.EvidenceReference, "evidenceReference", 256),
            EvidenceSha256 = Sha256(request.EvidenceSha256),
            ReviewedBy = Required(reviewer, "reviewer", 128),
            ReviewedAtUtc = now,
            RuleVersion = Required(request.RuleVersion, "ruleVersion", 128),
            ValidUntilUtc = Utc(request.ValidUntilUtc, "validUntilUtc"),
            Reason = Required(request.Reason, "reason", 300),
            PreviousReviewId = request.PreviousReviewId ?? Guid.Empty
        };
        Validate(review, now);

        var existing = await db.CreatorOnboardingReviews.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == review.Id);
        if (existing != null)
            return Match(existing, review);

        var latest = await GetLatest(review.CreatorUserId);
        if ((latest?.Id ?? Guid.Empty) != review.PreviousReviewId)
            throw new RewardProgramException(
                "previousReviewId must identify the creator's latest review");
        if (!guardianAcceptance
            && review.RepresentativeAcceptedAtUtc != null
            && (latest == null
                || latest.RepresentativeAccountId != review.RepresentativeAccountId
                || latest.RepresentativeAgreementHash
                    != review.RepresentativeAgreementHash
                || latest.RepresentativeAcceptedAtUtc
                    != review.RepresentativeAcceptedAtUtc))
            throw new RewardProgramException(
                "Representative acceptance may be recorded only through the guardian endpoint");
        if (review.Status == CreatorOnboardingStatus.Approved
            && review.CapacityStatus == CreatorCapacityStatus.Minor16PlusWithGuardian
            && (latest == null
                || latest.Status is not (CreatorOnboardingStatus.Pending
                    or CreatorOnboardingStatus.Approved)
                || latest.RepresentativeAcceptedAtUtc == null
                || latest.RepresentativeAccountId
                    != review.RepresentativeAccountId
                || latest.RepresentativeAgreementHash
                    != review.RepresentativeAgreementHash
                || latest.RepresentativeAcceptedAtUtc
                    != review.RepresentativeAcceptedAtUtc))
            throw new RewardProgramException(
                "Minor approval must follow the recorded representative acceptance");

        db.CreatorOnboardingReviews.Add(review);
        try
        {
            await db.SaveChangesAsync();
            return review;
        }
        catch (DbUpdateException)
        {
            db.Entry(review).State = EntityState.Detached;
            existing = await db.CreatorOnboardingReviews.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == review.Id);
            if (existing == null)
                throw;
            return Match(existing, review);
        }
    }

    public async Task<CreatorEligibility> GetEligibility(
        string creatorUserId,
        string minecraftUuid,
        string agreementHash = null)
    {
        creatorUserId = Required(creatorUserId, "creatorUserId", 128);
        minecraftUuid = MinecraftUuid(minecraftUuid);
        var review = await GetLatest(creatorUserId);
        if (review == null)
            return new(false, false);

        var now = DateTime.UtcNow;
        var capacityValid = review.CapacityStatus == CreatorCapacityStatus.AdultDeclared
                && review.RepresentativeAccountId == null
            || review.CapacityStatus == CreatorCapacityStatus.Minor16PlusWithGuardian
                && review.RepresentativeAcceptedAtUtc.HasValue
                && IsSha256(agreementHash)
                && string.Equals(
                    review.RepresentativeAgreementHash,
                    agreementHash,
                    StringComparison.OrdinalIgnoreCase);
        var eligible = review.MinecraftUuid == minecraftUuid
            && review.Status == CreatorOnboardingStatus.Approved
            && (review.ValidUntilUtc == null || review.ValidUntilUtc > now)
            && capacityValid;
        var paidPublicationReady = eligible
            && PaidCountry(review.ResidenceCountry)
            && PaidCountry(review.TaxResidenceCountry)
            && PaidCapacityJurisdiction(
                review.ResidenceCountry,
                review.CapacityJurisdiction,
                review.SellerType)
            && review.TaxDocumentRoute != CreatorTaxDocumentRoute.NotApplicable
            && ValidTaxRoute(review.TaxResidenceCountry, review.SellerType,
                review.TaxDocumentRoute)
            && (review.SellerType == CreatorSellerType.Individual
                || review.VerificationReference != null);
        return new(eligible, paidPublicationReady);
    }

    public async Task<CreatorOnboardingReview> AcceptGuardian(
        Guid previousReviewId,
        GuardianAcceptanceRequest request,
        string representativeAccountId)
    {
        var previous = await db.CreatorOnboardingReviews.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == previousReviewId)
            ?? throw new RewardProgramException("Creator review was not found");
        representativeAccountId = Required(
            representativeAccountId, "representativeAccountId", 32);
        var locale = request?.Locale?.Trim().ToLowerInvariant();
        if (locale is not ("en" or "de"))
            throw new RewardProgramException("locale must be en or de");
        var agreementHash = Sha256(request.AgreementHash);
        var existing = await db.CreatorOnboardingReviews.AsNoTracking()
            .SingleOrDefaultAsync(item => item.PreviousReviewId == previousReviewId);
        if (existing != null)
        {
            if (existing.RepresentativeAccountId == representativeAccountId
                && existing.RepresentativeAgreementHash == agreementHash
                && existing.RepresentativeAcceptedAtUtc != null
                && existing.ReviewedBy == representativeAccountId)
                return existing;
            throw new RewardProgramException(
                "This guardian request is no longer current");
        }
        var latest = await GetLatest(previous.CreatorUserId);
        if (latest.Id != previousReviewId
            || latest.Status != CreatorOnboardingStatus.Pending
            || latest.CapacityStatus != CreatorCapacityStatus.Minor16PlusWithGuardian
            || latest.RepresentativeAcceptedAtUtc != null
            || latest.RepresentativeAccountId != representativeAccountId)
            throw new RewardProgramException(
                "This guardian request is not the current pending review");

        return await Append(new(
            Guid.NewGuid(), latest.CreatorUserId, latest.MinecraftUuid,
            latest.Status, latest.ResidenceCountry, latest.TaxResidenceCountry,
            latest.SellerType, latest.CapacityJurisdiction, latest.CapacityStatus,
            latest.TaxDocumentRoute, latest.PrivacyNoticeVersion,
            latest.VerificationReference,
            latest.RepresentativeAccountId,
            agreementHash, DateTime.UtcNow,
            latest.EvidenceReference, latest.EvidenceSha256, latest.RuleVersion,
            latest.ValidUntilUtc,
            $"Legal representative accepted the pinned Creator agreement ({locale}, Discord button)",
            latest.Id), representativeAccountId, true);
    }

    public async Task<CreatorOnboardingReview> GetLatest(string creatorUserId)
    {
        creatorUserId = Required(creatorUserId, "creatorUserId", 128);
        return await db.CreatorOnboardingReviews.AsNoTracking()
            .Where(item => item.CreatorUserId == creatorUserId
                && !db.CreatorOnboardingReviews.Any(next =>
                    next.PreviousReviewId == item.Id))
            .SingleOrDefaultAsync();
    }

    private static void Validate(CreatorOnboardingReview review, DateTime now)
    {
        if (!Enum.IsDefined(review.Status)
            || !Enum.IsDefined(review.SellerType)
            || !Enum.IsDefined(review.CapacityStatus)
            || !Enum.IsDefined(review.TaxDocumentRoute))
            throw new RewardProgramException("Review status values are invalid");
        if (review.Status == CreatorOnboardingStatus.Approved
            && review.CapacityJurisdiction != review.ResidenceCountry
            && !review.CapacityJurisdiction.StartsWith(
                $"{review.ResidenceCountry}-", StringComparison.Ordinal))
            throw new RewardProgramException(
                "The capacity jurisdiction must match the residence country");
        if (review.Status == CreatorOnboardingStatus.Approved
            && review.TaxDocumentRoute != CreatorTaxDocumentRoute.NotApplicable
            && (!PaidCountry(review.ResidenceCountry)
                || !PaidCountry(review.TaxResidenceCountry)
                || !PaidCapacityJurisdiction(
                    review.ResidenceCountry,
                    review.CapacityJurisdiction,
                    review.SellerType)))
            throw new RewardProgramException(
                "Paid creators must use a supported country and capacity jurisdiction");
        if (review.Status == CreatorOnboardingStatus.Approved
            && review.ValidUntilUtc.HasValue && review.ValidUntilUtc <= now)
            throw new RewardProgramException("validUntilUtc must be in the future");
        if (review.CapacityStatus == CreatorCapacityStatus.Minor16PlusWithGuardian
            && (string.IsNullOrWhiteSpace(review.RepresentativeAccountId)
                || (review.RepresentativeAgreementHash == null)
                    != (review.RepresentativeAcceptedAtUtc == null)
                || review.RepresentativeAgreementHash != null
                    && !IsSha256(review.RepresentativeAgreementHash)))
            throw new RewardProgramException(
                "Minor capacity requires a representative account and complete acceptance evidence");
        if (review.Status == CreatorOnboardingStatus.Approved
            && review.CapacityStatus == CreatorCapacityStatus.Minor16PlusWithGuardian
            && review.RepresentativeAcceptedAtUtc == null)
            throw new RewardProgramException(
                "Minor approval requires separate representative acceptance");
        if (review.Status == CreatorOnboardingStatus.Approved
            && review.CapacityStatus == CreatorCapacityStatus.Insufficient)
            throw new RewardProgramException(
                "A creator without sufficient declared capacity cannot be approved");
        if (review.CapacityStatus == CreatorCapacityStatus.AdultDeclared
            && (review.RepresentativeAccountId != null
                || review.RepresentativeAgreementHash != null
                || review.RepresentativeAcceptedAtUtc != null))
            throw new RewardProgramException(
                "Adult capacity cannot contain representative acceptance");
        if (review.SellerType == CreatorSellerType.Business
            && review.CapacityStatus != CreatorCapacityStatus.AdultDeclared)
            throw new RewardProgramException(
                "A business Creator requires an adult authorized signatory");
        if (review.TaxDocumentRoute != CreatorTaxDocumentRoute.NotApplicable
            && !ValidTaxRoute(
                review.TaxResidenceCountry,
                review.SellerType,
                review.TaxDocumentRoute))
            throw new RewardProgramException(
                "The tax document route does not match the tax residence");
        if (review.TaxDocumentRoute == CreatorTaxDocumentRoute.UkSelfBilling
            && (review.ValidUntilUtc == null
                || review.ValidUntilUtc > now.AddDays(366)))
            throw new RewardProgramException(
                "Self-billing approval must expire for review within 12 months");
    }

    private static CreatorOnboardingReview Match(
        CreatorOnboardingReview existing,
        CreatorOnboardingReview requested)
    {
        if (existing.CreatorUserId != requested.CreatorUserId
            || existing.MinecraftUuid != requested.MinecraftUuid
            || existing.Status != requested.Status
            || existing.ResidenceCountry != requested.ResidenceCountry
            || existing.TaxResidenceCountry != requested.TaxResidenceCountry
            || existing.SellerType != requested.SellerType
            || existing.CapacityJurisdiction != requested.CapacityJurisdiction
            || existing.CapacityStatus != requested.CapacityStatus
            || existing.TaxDocumentRoute != requested.TaxDocumentRoute
            || existing.PrivacyNoticeVersion != requested.PrivacyNoticeVersion
            || existing.VerificationReference != requested.VerificationReference
            || existing.RepresentativeAccountId != requested.RepresentativeAccountId
            || existing.RepresentativeAgreementHash != requested.RepresentativeAgreementHash
            || existing.RepresentativeAcceptedAtUtc != requested.RepresentativeAcceptedAtUtc
            || existing.EvidenceReference != requested.EvidenceReference
            || existing.EvidenceSha256 != requested.EvidenceSha256
            || existing.ReviewedBy != requested.ReviewedBy
            || existing.RuleVersion != requested.RuleVersion
            || existing.ValidUntilUtc != requested.ValidUntilUtc
            || existing.Reason != requested.Reason
            || existing.PreviousReviewId != requested.PreviousReviewId)
            throw new RewardProgramException(
                "reviewId was already used for another review");
        return existing;
    }

    private static string Required(string value, string name, int maxLength)
    {
        value = value?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length > maxLength)
            throw new RewardProgramException(
                $"{name} must contain 1 to {maxLength} characters");
        return value;
    }

    private static string Country(string value, string name)
    {
        value = value?.Trim().ToUpperInvariant();
        if (value?.Length != 2 || value.Any(character => character is < 'A' or > 'Z'))
            throw new RewardProgramException($"{name} must be an ISO alpha-2 country code");
        return value;
    }

    private static string Jurisdiction(string value, string name)
    {
        value = value?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(value) || value.Length > 8
            || value.Any(character => character != '-'
                && (character is < 'A' or > 'Z')))
            throw new RewardProgramException(
                $"{name} must be a country or supported subdivision code");
        return value;
    }

    private static string Optional(string value, string name, int maxLength)
    {
        value = value?.Trim();
        if (value?.Length > maxLength)
            throw new RewardProgramException(
                $"{name} must contain at most {maxLength} characters");
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static string OptionalSha256(string value, string name)
    {
        value = Optional(value, name, 64)?.ToLowerInvariant();
        if (value != null && !IsSha256(value))
            throw new RewardProgramException($"{name} must be a SHA-256 digest");
        return value;
    }

    private static string VerificationReference(string value)
    {
        value = Optional(value, "verificationReference", 256);
        var separator = value?.IndexOf(':') ?? -1;
        if (value != null && (separator < 1 || separator == value.Length - 1))
            throw new RewardProgramException(
                "verificationReference must include its provider namespace");
        return value;
    }

    private static bool IsSha256(string value) =>
        value?.Length == 64 && value.All(Uri.IsHexDigit);

    private static bool PaidCountry(string country) =>
        country == "GB" || country == "US" || country == "CH"
            || EuCountries.Contains(country);

    private static bool PaidCapacityJurisdiction(
        string country,
        string jurisdiction,
        CreatorSellerType sellerType)
    {
        var supported = jurisdiction is "GB-ENG" or "GB-WLS" or "CH"
            || jurisdiction == "US"
            || jurisdiction.Length == 5 && jurisdiction.StartsWith("US-")
            || jurisdiction.Length == 2 && EuCountries.Contains(jurisdiction);
        if (!supported || sellerType == CreatorSellerType.Business)
            return supported;
        return country switch
        {
            "GB" => jurisdiction.StartsWith("GB-"),
            "US" => jurisdiction == "US" || jurisdiction.StartsWith("US-"),
            _ => jurisdiction == country
        };
    }

    private static bool ValidTaxRoute(
        string country,
        CreatorSellerType sellerType,
        CreatorTaxDocumentRoute route) => country switch
        {
            "GB" => sellerType == CreatorSellerType.Business
                ? route is CreatorTaxDocumentRoute.Statement
                    or CreatorTaxDocumentRoute.UkSelfBilling
                : route == CreatorTaxDocumentRoute.Statement,
            "US" => route == CreatorTaxDocumentRoute.UsSettlement,
            "CH" => route == (sellerType == CreatorSellerType.Business
                ? CreatorTaxDocumentRoute.CreatorInvoice
                : CreatorTaxDocumentRoute.Statement),
            _ when EuCountries.Contains(country) =>
                route == (sellerType == CreatorSellerType.Business
                    ? CreatorTaxDocumentRoute.CreatorInvoice
                    : CreatorTaxDocumentRoute.Statement),
            _ => false
        };

    private static string MinecraftUuid(string value)
    {
        value = value?.Replace("-", "").Trim().ToLowerInvariant();
        if (value?.Length != 32 || value.Any(character => !Uri.IsHexDigit(character)))
            throw new RewardProgramException("minecraftUuid must be a Minecraft UUID");
        return value;
    }

    private static string Sha256(string value)
    {
        value = value?.Trim().ToLowerInvariant();
        if (value?.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
            throw new RewardProgramException("evidenceSha256 must be a SHA-256 digest");
        return value;
    }

    private static DateTime? Utc(DateTime? value, string name)
    {
        if (value.HasValue && value.Value.Kind == DateTimeKind.Local)
            throw new RewardProgramException($"{name} must be UTC");
        return value == null ? null : new DateTime(
            value.Value.Ticks - value.Value.Ticks % 10,
            DateTimeKind.Utc);
    }
}
