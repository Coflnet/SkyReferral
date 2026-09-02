using System;
using System.ComponentModel.DataAnnotations;

namespace Coflnet.Sky.Referral.Models;

public enum CreatorOnboardingStatus
{
    Pending = 1,
    Approved = 2,
    Suspended = 3,
    Rejected = 4
}

public enum CreatorSellerType
{
    Individual = 1,
    Business = 2
}

public enum CreatorCapacityStatus
{
    AdultDeclared = 1,
    Minor16PlusWithGuardian = 2,
    Insufficient = 3
}

public enum CreatorTaxDocumentRoute
{
    NotApplicable = 1,
    Statement = 2,
    CreatorInvoice = 3,
    UkSelfBilling = 4,
    UsSettlement = 5
}

/// <summary>
/// Immutable outcome of a manual creator review. Identity documents stay with
/// the evidence system; this table keeps only an opaque reference and digest.
/// </summary>
public class CreatorOnboardingReview
{
    public Guid Id { get; set; }
    [Required, MaxLength(128)]
    public string CreatorUserId { get; set; }
    [Required, MaxLength(32)]
    public string MinecraftUuid { get; set; }
    public CreatorOnboardingStatus Status { get; set; }
    [Required, MaxLength(2)]
    public string ResidenceCountry { get; set; }
    [Required, MaxLength(2)]
    public string TaxResidenceCountry { get; set; }
    public CreatorSellerType SellerType { get; set; }
    [Required, MaxLength(8)]
    public string CapacityJurisdiction { get; set; }
    public CreatorCapacityStatus CapacityStatus { get; set; }
    public CreatorTaxDocumentRoute TaxDocumentRoute { get; set; }
    [Required, MaxLength(64)]
    public string PrivacyNoticeVersion { get; set; }
    [MaxLength(256)]
    public string VerificationReference { get; set; }
    [MaxLength(32)]
    public string RepresentativeAccountId { get; set; }
    [MaxLength(64)]
    public string RepresentativeAgreementHash { get; set; }
    public DateTime? RepresentativeAcceptedAtUtc { get; set; }
    [Required, MaxLength(256)]
    public string EvidenceReference { get; set; }
    [Required, MaxLength(64)]
    public string EvidenceSha256 { get; set; }
    [Required, MaxLength(128)]
    public string ReviewedBy { get; set; }
    public DateTime ReviewedAtUtc { get; set; }
    [Required, MaxLength(128)]
    public string RuleVersion { get; set; }
    public DateTime? ValidUntilUtc { get; set; }
    [Required, MaxLength(300)]
    public string Reason { get; set; }
    public Guid PreviousReviewId { get; set; }
}
