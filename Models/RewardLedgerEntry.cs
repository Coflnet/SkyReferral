using System;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Coflnet.Sky.Referral.Models;

public enum RewardLedgerEntryKind
{
    Pending = 1,
    Award = 2,
    Correction = 3,
    PayoutRequest = 4,
    Payout = 5,
    Cancellation = 6
}

public enum RewardSource
{
    Report = 1,
    Referral = 2,
    CreatorCode = 3,
    CreatorFee = 4
}

/// <summary>
/// One event in the append-oriented EUR remuneration ledger. Pending creator
/// fees, approved rewards, corrections and settlements all use this table.
/// </summary>
public class RewardLedgerEntry
{
    public Guid Id { get; set; }
    [Required, MaxLength(200)]
    public string Reference { get; set; }
    [MaxLength(128)]
    public string RewardAccountId { get; set; }
    public RewardLedgerEntryKind Kind { get; set; }
    public RewardSource? Source { get; set; }
    public long RemunerationEurCents { get; set; }
    public long? PayoutThresholdEurCents { get; set; }
    public Guid? RelatedEntryId { get; set; }
    [MaxLength(128)]
    public string OfferVersion { get; set; }
    [JsonIgnore, MaxLength(64), ConcurrencyCheck]
    public string ClaimCodeHash { get; set; }
    public bool WasAnonymous { get; set; }
    public DateTime? ClaimedAt { get; set; }
    [Required, MaxLength(300)]
    public string Reason { get; set; }
    [Required, MaxLength(128)]
    public string CreatedBy { get; set; }
    [MaxLength(4000)]
    public string DetailsJson { get; set; }
    public DateTime CreatedAt { get; set; }
}
