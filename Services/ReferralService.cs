using System.Threading.Tasks;
using Coflnet.Sky.Referral.Models;
using System;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Coflnet.Payments.Client.Api;
using System.Collections.Generic;
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
        private readonly double referralBonusPercent = 0.25;

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
        }

        /// <summary>
        /// Adds a new referred user to the database
        /// </summary>
        /// <param name="userId"></param>
        /// <param name="referredUser"></param>
        /// <returns></returns>
        /// <exception cref="ApiException"></exception>
        public async Task<ReferralElement> AddReferral(string userId, string referredUser)
        {
            var flipFromDb = await db.Referrals.Where(f => f.Invited == referredUser).FirstOrDefaultAsync();
            if (flipFromDb != null)
                if (flipFromDb.Inviter == userId)
                    throw new ApiException("You already used that referral link");
                else
                    throw new ApiException("You have already used a referral link");
            ReferralElement flip = await CreateNewRef(userId, referredUser);
            return flip;
        }

        private async Task<ReferralElement> CreateNewRef(string userId, string referredUser)
        {
            var flip = new ReferralElement() { Inviter = userId, Invited = referredUser };
            db.Referrals.Add(flip);
            await db.SaveChangesAsync();
            return flip;
        }

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
        /// Awards percentage of coins to inviter if first purchase
        /// </summary>
        /// <param name="userId"></param>
        /// <param name="size"></param>
        /// <param name="reference"></param>
        /// <param name="productSlug"></param>
        /// <returns></returns>
        public async Task NewPurchase(string userId, double size, string reference, string productSlug)
        {
            if (productSlug == config["PRODUCTS:VERIFY_MC"] || productSlug == config["PRODUCTS:TEST_PREMIUM"] || productSlug == config["PRODUCTS:TRANSFER"])
                return; // don't hand out the referral bonus for the verify bonus
            var rewardSize = Math.Abs(Convert.ToInt32(Math.Round(size * referralBonusPercent)));
            var user = await GetUserAndAwardBonusToInviter(userId, ReferralFlags.FIRST_PURCHASE_BONUS, rewardSize);
            // nothing more todo :) (maybe give extra bonus to new user in the future)
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
            var user = await GetUserAndAwardBonusToInviter(userId, ReferralFlags.VERIFIED_MC_ACCOUNT, rewardSize: 200);
            // give user 24 hours of special premium
            var optionName = config["PRODUCTS:VERIFY_MC"];
            var amount = 100;
            await TopupAmount(userId, minecraftUuid, optionName, amount);
            var productName = config["PRODUCTS:TEST_PREMIUM"];
            await ExecuteSwollowDupplicate(async () =>
            {
                try
                {
                    await paymentUserApi.UserUserIdServicePurchaseProductSlugPostAsync(userId, productName, minecraftUuid);
                    logger.LogInformation($"successfully purchased test premium for user {userId}");
                }
                catch (System.Exception e)
                {
                    if (e.Message.Contains("insuficcient balance"))
                    {
                        logger.LogError($"User {userId} didn't have enough balance to get test premium for {minecraftUuid} (db id: {user.Id}");
                        return;
                    }
                    throw;
                }
            });
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
                throw e;
            }
        }

        private async Task<ReferralElement> GetUserAndAwardBonusToInviter(string userId, ReferralFlags flag, int rewardSize)
        {
            var refElem = await db.Referrals.Where(r => r.Invited == userId).FirstOrDefaultAsync();
            if (refElem == null)
            {
                // this user has no registered ref but just validated
                // thereby this user can't be referred anymore
                logger.LogInformation("adding not referred user");
                refElem = await CreateNewRef(null, userId);
            }
            else if (!refElem.Flags.HasFlag(flag))
            {
                // award coins to inviter
                var inviter = refElem.Inviter;
                if (flag == ReferralFlags.FIRST_PURCHASE_BONUS)
                    refElem.PurchaseAmount = (int)(rewardSize / referralBonusPercent);
                if (inviter != null)
                {
                    // check for ref spam
                    var invitedUsers = await db.Referrals.Where(r => r.Inviter == inviter && r.Flags > 0 && r.CreatedAt > DateTime.Now.AddDays(-30)).ToListAsync();
                    if (invitedUsers.Count >= 7 && !invitedUsers.Any(i => i.PurchaseAmount > 1700) && flag == ReferralFlags.VERIFIED_MC_ACCOUNT)
                    {
                        logger.LogInformation($"User {inviter} has invited {invitedUsers.Count} users without any premium purchases the last 30 days, not giving any awards");
                    }
                    else
                    {
                        await TopupAmount(inviter, $"{userId}+{flag}", config["PRODUCTS:REFERAL_BONUS"], rewardSize);
                        logger.LogInformation($"User {inviter} has invited {invitedUsers.Count} users in the last 30 days");
                    }
                }
            }
            refElem.Flags |= flag;
            await db.SaveChangesAsync();

            return refElem;
        }
    }
}
