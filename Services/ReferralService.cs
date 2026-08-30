using System.Threading.Tasks;
using Coflnet.Sky.Referral.Models;
using System;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Coflnet.Payments.Client.Api;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Coflnet.Sky.Referral.Services
{
    public class ApiException : Coflnet.Sky.Core.CoflnetException
    {
        public ApiException(string message) : base("referral", message)
        {
        }
    }
    public class ReferralService
    {
        private ReferralDbContext db;
        private TopUpApi topUpApi;
        private UserApi paymentUserApi;
        private ProductsApi productsApi;
        private IConfiguration config;
        private readonly ILogger<ReferralService> logger;
        private readonly string referralProgramVersion;

        /// <summary>
        /// Creates a new instance of the referral service
        /// </summary>
        /// <param name="db"></param>
        /// <param name="topUpApi"></param>
        /// <param name="paymentUserApi"></param>
        /// <param name="productsApi"></param>
        /// <param name="config"></param>
        /// <param name="logger"></param>
        public ReferralService(ReferralDbContext db, TopUpApi topUpApi, UserApi paymentUserApi, ProductsApi productsApi, IConfiguration config, ILogger<ReferralService> logger)
        {
            this.db = db;
            this.topUpApi = topUpApi;
            this.paymentUserApi = paymentUserApi;
            this.productsApi = productsApi;
            this.config = config;
            this.logger = logger;
            referralProgramVersion = config["REFERRAL_PROGRAM_VERSION"]?.Trim();
            if (!IsProgramVersionConfigured(referralProgramVersion))
                throw new InvalidOperationException(
                    "REFERRAL_PROGRAM_VERSION must contain 1 to 32 characters");
        }

        /// <summary>
        /// Adds a new referred user to the database
        /// </summary>
        /// <param name="userId"></param>
        /// <param name="referredUser"></param>
        /// <param name="programVersion"></param>
        /// <param name="locale"></param>
        /// <returns></returns>
        /// <exception cref="ApiException"></exception>
        public async Task<ReferralElement> AddReferral(
            string userId,
            string referredUser,
            string programVersion,
            string locale)
        {
            if (!string.Equals(
                    programVersion,
                    referralProgramVersion,
                    StringComparison.Ordinal))
                throw new ApiException(
                    "The referral offer changed. Review the current offer and try again.");
            if (string.IsNullOrWhiteSpace(locale) || locale.Length > 35)
                throw new ApiException("The referral offer locale is missing or invalid.");
            string normalizedLocale;
            try
            {
                normalizedLocale = CultureInfo.GetCultureInfo(locale).Name;
            }
            catch (CultureNotFoundException)
            {
                throw new ApiException("The referral offer locale is missing or invalid.");
            }
            if (string.IsNullOrEmpty(normalizedLocale))
                throw new ApiException("The referral offer locale is missing or invalid.");
            if(userId == referredUser)
                throw new ApiException("You can't refer yourself");
            var flipFromDb = await db.Referrals.Where(f => f.Invited == referredUser).FirstOrDefaultAsync();
            if (flipFromDb != null)
                if (flipFromDb.Inviter == userId)
                    return flipFromDb;
                else
                    throw new ApiException("You have already used another referral link");
            ReferralElement flip = await CreateNewRef(
                userId,
                referredUser,
                normalizedLocale,
                programVersion);
            return flip;
        }

        private async Task<ReferralElement> CreateNewRef(
            string userId,
            string referredUser,
            string locale,
            string programVersion)
        {
            var now = DateTime.UtcNow;
            var flip = new ReferralElement()
            {
                Inviter = userId,
                Invited = referredUser,
                ProgramVersion = userId == null ? null : programVersion,
                Locale = userId == null ? null : locale,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.Referrals.Add(flip);
            await db.SaveChangesAsync();
            return flip;
        }

        internal static bool IsProgramVersionConfigured(string value) =>
            !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= 32;

        /// <summary>
        /// Returns a summary of the referrals for the given user
        /// </summary>
        /// <param name="userId"></param>
        /// <returns></returns>
        public async Task<RefInfo> GetRefInfo(string userId)
        {
            var refedBy = await db.Referrals.Where(r => r.Invited == userId).FirstOrDefaultAsync();
            var referrals = await db.Referrals.Where(r => r.Inviter == userId).ToListAsync();
            return new RefInfo()
            {
                Invited = referrals,
                Inviter = refedBy
            };
        }

        /// <summary>
        /// User verified his minecraft account
        /// </summary>
        /// <param name="userId"></param>
        /// <param name="minecraftUuid"></param>
        /// <param name="exisitngCount"></param>
        /// <returns></returns>
        public async Task Verified(string userId, string minecraftUuid, int exisitngCount)
        {
            logger.LogInformation($"Verified user {userId} with account {minecraftUuid}");
            if (exisitngCount != 0)
            {
                logger.LogInformation($"Account {minecraftUuid} already has {exisitngCount} connections, not giving any awards");
                return; // don't award
            }
            var user = await BeginVerification(userId);
            if (user.Flags.HasFlag(ReferralFlags.VERIFIED_MC_ACCOUNT))
                return;
            await ApplyVerificationOnboarding(userId, minecraftUuid);
            await CompleteVerification(user);
        }

        protected virtual async Task ApplyVerificationOnboarding(
            string userId,
            string minecraftUuid)
        {
            // Give the new user 100 CoflCoins and spend them on the configured
            // test-premium period. The Minecraft UUID makes payment retries
            // idempotent.
            var optionName = config["PRODUCTS:VERIFY_MC"];
            var amount = 100;
            await TopupAmount(userId, minecraftUuid, optionName, amount);
            var productName = config["PRODUCTS:TEST_PREMIUM"];
            await ExecuteSwollowDupplicate(() =>
                paymentUserApi.UserUserIdServicePurchaseProductSlugPostAsync(
                    userId,
                    productName,
                    minecraftUuid));
            logger.LogInformation("Successfully purchased test premium for user {UserId}", userId);
        }

        private async Task TopupAmount(string userId, string reference, string optionName, int amount = 0)
        {
            var topupOptions = await productsApi.ProductsTopupGetAsync(0, 200);
            if (topupOptions == null)
                throw new ApiException("Could not get topup options from payment service");
            var topupInvite = topupOptions.Where(t => t.Slug == optionName).FirstOrDefault();
            if (topupInvite == null)
                throw new ApiException($"Custom topuOption {optionName} doesn't exist");
            await ExecuteSwollowDupplicate(async () =>
            {
                logger.LogInformation($"Toping up {amount} to {userId} with product {optionName}");
                await topUpApi.TopUpCustomPostAsync(userId, new Payments.Client.Model.CustomTopUp()
                {
                    ProductId = topupInvite.Slug,
                    Reference = reference,
                    Amount = amount
                });
            });
        }

        private async Task ExecuteSwollowDupplicate(Func<Task> action)
        {
            try
            {
                await action();
            }
            catch (Exception e)
            {
                if (e.Message.Contains("This transaction already happened"))
                {

                    logger.LogInformation("swollowing dupplicate transaction");
                    return;
                }
                throw;
            }
        }

        internal async Task<ReferralElement> BeginVerification(string userId)
        {
            var refElem = await db.Referrals.Where(r => r.Invited == userId).FirstOrDefaultAsync();
            if (refElem == null)
            {
                // this user has no registered ref but just validated
                // thereby this user can't be referred anymore
                logger.LogInformation("adding not referred user");
                refElem = await CreateNewRef(null, userId, null, null);
            }
            return refElem;
        }

        internal async Task CompleteVerification(ReferralElement refElem)
        {
            refElem.Flags |= ReferralFlags.VERIFIED_MC_ACCOUNT;
            refElem.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

    }
}
