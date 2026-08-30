using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Sky.Referral.Controllers;
using Coflnet.Sky.Referral.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Coflnet.Sky.Referral.Services;

public class ReferralServiceTests
{
    private const string ProgramVersion = "new-user-100-v1";

    [Test]
    public void VerificationUsesThePersistedProductSlugs()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json")
            .Build();

        Assert.Multiple(() =>
        {
            Assert.That(configuration["PRODUCTS:VERIFY_MC"], Is.EqualTo("verify_mc"));
            Assert.That(configuration["PRODUCTS:TEST_PREMIUM"], Is.EqualTo("test-premium"));
        });
    }

    [Test]
    public async Task NewReferralRecordsServerConfiguredProgramVersion()
    {
        var options = new DbContextOptionsBuilder<ReferralDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new ReferralDbContext(options);

        var referral = await NewService(db).AddReferral(
            "inviter",
            "referred",
            ProgramVersion,
            "en");

        Assert.Multiple(() =>
        {
            Assert.That(referral.ProgramVersion, Is.EqualTo(ProgramVersion));
            Assert.That(referral.Locale, Is.EqualTo("en"));
        });
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("123456789012345678901234567890123")]
    public void ReferralProgramVersionFailsClosed(string value)
    {
        Assert.That(ReferralService.IsProgramVersionConfigured(value), Is.False);
    }

    [Test]
    public void ReferralMutationRequiresDisplayedOfferMetadata()
    {
        var action = typeof(ReferralController).GetMethods()
            .Single(method => method.Name == nameof(ReferralController.TrackReferral)
                && method.GetCustomAttributes(typeof(HttpPostAttribute), true).Any());
        var serviceMethod = typeof(ReferralService).GetMethods()
            .Single(method => method.Name == nameof(ReferralService.AddReferral));

        Assert.Multiple(() =>
        {
            Assert.That(action.GetParameters().Select(parameter => parameter.Name),
                Is.EqualTo(new[]
                    { "userId", "referedUser", "programVersion", "locale" }));
            Assert.That(serviceMethod.GetParameters().Select(parameter => parameter.Name),
                Is.EqualTo(new[]
                    { "userId", "referredUser", "programVersion", "locale" }));
            Assert.That(typeof(ReferralElement).GetProperty("Locale"), Is.Not.Null);
        });
    }

    [Test]
    public void ReferralMutationRejectsStaleProgramVersion()
    {
        var options = new DbContextOptionsBuilder<ReferralDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        using var db = new ReferralDbContext(options);

        Assert.ThrowsAsync<ApiException>(() => NewService(db).AddReferral(
            "inviter",
            "referred",
            "stale-version",
            "en"));
    }

    [Test]
    public void ReferralMutationRejectsMissingProgramVersion()
    {
        using var db = NewDb();

        Assert.ThrowsAsync<ApiException>(() => NewService(db).AddReferral(
            "inviter",
            "referred",
            null,
            "en"));
    }

    [Test]
    public void ReferralMutationRejectsMissingLocale()
    {
        using var db = NewDb();

        Assert.ThrowsAsync<ApiException>(() => NewService(db).AddReferral(
            "inviter",
            "referred",
            ProgramVersion,
            null));
    }

    [Test]
    public async Task VerificationOnboardingRunsOnlyOncePerNewUser()
    {
        await using var db = NewDb();
        db.Referrals.Add(new ReferralElement
        {
            Inviter = "inviter",
            Invited = "new-user"
        });
        await db.SaveChangesAsync();

        var service = new RecordingReferralService(db);
        await service.Verified("new-user", "minecraft-uuid-1", 0);
        await service.Verified("new-user", "minecraft-uuid-2", 0);
        var referral = await db.Referrals.SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(service.PaymentReferences,
                Is.EqualTo(new[] { "minecraft-uuid-1" }));
            Assert.That(
                referral.Flags.HasFlag(ReferralFlags.VERIFIED_MC_ACCOUNT),
                Is.True);
            Assert.That(referral.Inviter, Is.EqualTo("inviter"));
        });
    }

    [Test]
    public async Task VerificationReplayDoesNotGrantAgain()
    {
        await using var db = NewDb();
        var service = new RecordingReferralService(db);

        await service.Verified("new-user", "minecraft-uuid", 0);
        await service.Verified("new-user", "minecraft-uuid", 0);

        Assert.That(service.PaymentReferences,
            Is.EqualTo(new[] { "minecraft-uuid" }));
    }

    [Test]
    public async Task PreviouslyLinkedMinecraftAccountDoesNotReceiveOnboarding()
    {
        await using var db = NewDb();
        var service = new RecordingReferralService(db);

        await service.Verified("new-user", "minecraft-uuid", 1);

        Assert.Multiple(() =>
        {
            Assert.That(service.PaymentReferences, Is.Empty);
            Assert.That(db.Referrals, Is.Empty);
        });
    }

    private static ReferralDbContext NewDb() => new(
        new DbContextOptionsBuilder<ReferralDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static ReferralService NewService(
        ReferralDbContext db,
        IReadOnlyDictionary<string, string> overrides = null) => new(
            db,
            null,
            null,
            null,
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    ["REFERRAL_PROGRAM_VERSION"] = ProgramVersion
                }.Concat(overrides ?? new Dictionary<string, string>())
                    .ToDictionary(pair => pair.Key, pair => pair.Value))
                .Build(),
            NullLogger<ReferralService>.Instance);

    private sealed class RecordingReferralService : ReferralService
    {
        public List<string> PaymentReferences { get; } = new();

        public RecordingReferralService(ReferralDbContext db)
            : base(
                db,
                null,
                null,
                null,
                new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string>
                    {
                        ["REFERRAL_PROGRAM_VERSION"] = ProgramVersion
                    })
                    .Build(),
                NullLogger<ReferralService>.Instance)
        {
        }

        protected override Task ApplyVerificationOnboarding(
            string userId,
            string minecraftUuid)
        {
            PaymentReferences.Add(minecraftUuid);
            return Task.CompletedTask;
        }
    }
}
