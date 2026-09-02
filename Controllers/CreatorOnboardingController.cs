using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Coflnet.Sky.Referral.Models;
using Coflnet.Sky.Referral.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace Coflnet.Sky.Referral.Controllers;

/// <summary>Internal API for immutable manual creator-review outcomes.</summary>
[ApiController]
[Route("api/creator-onboarding")]
public class CreatorOnboardingController : ControllerBase
{
    private const string ReviewerAccountId = "discord:267680402594988033";
    private readonly CreatorOnboardingService onboarding;
    private readonly IConfiguration configuration;

    public CreatorOnboardingController(
        CreatorOnboardingService onboarding,
        IConfiguration configuration)
    {
        this.onboarding = onboarding;
        this.configuration = configuration;
    }

    [HttpGet("{creatorUserId}/eligibility")]
    public async Task<ActionResult<CreatorEligibility>> Eligibility(
        string creatorUserId,
        string minecraftUuid,
        string agreementHash = null)
    {
        if (!Authorized("READ_TOKEN"))
            return Unauthorized();
        return await onboarding.GetEligibility(
            creatorUserId, minecraftUuid, agreementHash);
    }

    [HttpPost("reviews")]
    public async Task<ActionResult<CreatorOnboardingReview>> Append(
        CreatorOnboardingReviewRequest request)
    {
        if (!AuthorizedReviewer())
            return Unauthorized();
        return await onboarding.Append(
            request, Request.Headers["X-Reviewer-Id"].ToString());
    }

    [HttpGet("{creatorUserId}/reviews/latest")]
    public async Task<ActionResult<CreatorOnboardingReview>> Latest(
        string creatorUserId)
    {
        if (!AuthorizedReviewer())
            return Unauthorized();
        var review = await onboarding.GetLatest(creatorUserId);
        return review == null ? NotFound() : review;
    }

    [HttpPost("reviews/{reviewId:guid}/guardian-acceptance")]
    public async Task<ActionResult<CreatorOnboardingReview>> AcceptGuardian(
        Guid reviewId,
        GuardianAcceptanceRequest request)
    {
        if (!Authorized("REVIEW_TOKEN"))
            return Unauthorized();
        return await onboarding.AcceptGuardian(
            reviewId, request, Request.Headers["X-Reviewer-Id"].ToString());
    }

    private bool Authorized(string key)
    {
        var expected = configuration[$"CREATOR_ONBOARDING:{key}"];
        var supplied = Request.Headers.Authorization.ToString();
        if (supplied.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            supplied = supplied[7..].Trim();
        return expected?.Length >= 32 && supplied.Length >= 32
            && CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(Encoding.UTF8.GetBytes(expected)),
                SHA256.HashData(Encoding.UTF8.GetBytes(supplied)));
    }

    private bool AuthorizedReviewer() => Authorized("REVIEW_TOKEN")
        && Request.Headers["X-Reviewer-Id"].ToString() == ReviewerAccountId;
}
