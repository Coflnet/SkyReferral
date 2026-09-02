using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Coflnet.Sky.Referral.Models;
using Coflnet.Sky.Referral.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace Coflnet.Sky.Referral.Controllers;

/// <summary>
/// Narrow internal API for the reward ledger. A facade authenticates end users
/// before forwarding claims or account reads with the writer credential.
/// </summary>
[ApiController]
[Route("api/rewards")]
public class RewardController : ControllerBase
{
    private readonly RewardProgramService rewards;
    private readonly IConfiguration configuration;

    public RewardController(RewardProgramService rewards, IConfiguration configuration)
    {
        this.rewards = rewards;
        this.configuration = configuration;
    }

    [HttpPost("entries")]
    public async Task<ActionResult<RewardEntryResult>> Append(RewardEntryRequest request)
    {
        var payout = request?.Kind is RewardLedgerEntryKind.PayoutRequest
            or RewardLedgerEntryKind.Payout;
        if (!Authorized(payout ? "PAYOUT_TOKEN" : "WRITE_TOKEN"))
            return Unauthorized();
        return await rewards.Append(request, configuration[payout
            ? "REWARDS:PAYOUT_ACTOR"
            : "REWARDS:WRITE_ACTOR"]);
    }

    [HttpGet("ready")]
    public async Task<IActionResult> Ready()
    {
        if (!Authorized("WRITE_TOKEN"))
            return Unauthorized();
        if (!configuration.GetValue<bool>("REWARDS:ENABLED"))
            return StatusCode(503);
        await rewards.CheckReady();
        return NoContent();
    }

    [HttpPost("claims")]
    public async Task<ActionResult> Claim(ClaimRewardRequest request)
    {
        if (!Authorized("WRITE_TOKEN"))
            return Unauthorized();
        if (request == null)
            throw new RewardProgramException("Request body is required");
        return Ok(await rewards.Claim(request.RewardAccountId, request.ClaimCode));
    }

    [HttpGet("accounts/{rewardAccountId}/balance")]
    public async Task<ActionResult> Balance(string rewardAccountId)
    {
        if (!Authorized("WRITE_TOKEN"))
            return Unauthorized();
        return Ok(await rewards.GetBalance(rewardAccountId));
    }

    [HttpGet("accounts/{rewardAccountId}/ledger")]
    public async Task<ActionResult> Ledger(string rewardAccountId)
    {
        if (!Authorized("WRITE_TOKEN"))
            return Unauthorized();
        return Ok(await rewards.GetLedger(rewardAccountId));
    }

    [HttpGet("liability")]
    public async Task<ActionResult> Liability(
        DateTime toExclusive,
        DateTime? fromInclusive = null,
        int skip = 0,
        int take = 500)
    {
        if (!Authorized("WRITE_TOKEN"))
            return Unauthorized();
        return Ok(await rewards.GetLiability(toExclusive, fromInclusive, skip, take));
    }

    private bool Authorized(string key)
    {
        var expected = configuration[$"REWARDS:{key}"];
        var supplied = Request.Headers.Authorization.ToString();
        if (supplied.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            supplied = supplied[7..].Trim();
        return expected?.Length >= 32 && supplied.Length >= 32
            && CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(Encoding.UTF8.GetBytes(expected)),
                SHA256.HashData(Encoding.UTF8.GetBytes(supplied)));
    }
}

public record ClaimRewardRequest(string RewardAccountId, string ClaimCode);
