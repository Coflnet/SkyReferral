using System;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Sky.Referral.Models;
using Microsoft.EntityFrameworkCore;

namespace Coflnet.Sky.Referral.Infrastructure;

internal static class DatabaseReadiness
{
    internal static async Task CheckAsync(ReferralDbContext database, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await database.Database.ExecuteSqlRawAsync("SELECT 1", timeout.Token);
        }
        catch (Exception)
        {
            throw new InvalidOperationException("The required database readiness check failed.");
        }
    }
}
