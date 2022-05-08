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
    public class ApiException : Exception
    {
        public ApiException(string message) : base(message)
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

        public ReferralService(ReferralDbContext db, TopUpApi topUpApi, UserApi paymentUserApi, ProductsApi productsApi, IConfiguration config, ILogger<ReferralService> logger)
        {
            this.db = db;
            this.topUpApi = topUpApi;
            this.paymentUserApi = paymentUserApi;
            this.productsApi = productsApi;
            this.config = config;
            this.logger = logger;
        }

        public async Task<ReferralElement> AddReferral(string userId, string referredUser)
        {
            var flipFromDb = await db.Referrals.Where(f => f.Invited == referredUser).FirstOrDefaultAsync();
            if (flipFromDb != null)
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
            if (productSlug == config["PRODUCTS:VERIFY_MC"] || productSlug == config["PRODUCTS:TEST_PREMIUM"])
                return; // don't hand out the referral bonus for the verify bonus
            var user = await GetUserAndAwardBonusToInviter(userId, ReferralFlags.FIRST_PURCHASE_BONUS, rewardSize: Convert.ToInt32(Math.Round(size / 4)));
            // nothing more todo :) (maybe give extra bonus to new user in the future)
        }


        public async Task Verified(string userId, string minecraftUuid)
        {
            logger.LogInformation($"Verified user {userId} with account {minecraftUuid}");
            var user = await GetUserAndAwardBonusToInviter(userId, ReferralFlags.VERIFIED_MC_ACCOUNT, rewardSize: 100);
            // give user 24 hours of special premium
            var optionName = config["PRODUCTS:VERIFY_MC"];
            var amount = 0;
            await TopupAmount(userId, minecraftUuid, optionName, amount);
            var productName = config["PRODUCTS:TEST_PREMIUM"];
            await ExecuteSwollowDupplicate(async () =>
            {
                try
                {
                    await paymentUserApi.UserUserIdPurchaseProductSlugPostAsync(userId, productName);
                }
                catch (System.Exception e)
                {
                    if (e.Message.Contains("insuficcient balance"))
                    {
                        logger.LogError($"User {userId} didn't have enough balance to get test premium for {minecraftUuid} (db id: {user.Id}");
                    }
                    throw;
                }
            });
        }

        private async Task TopupAmount(string userId, string reference, string optionName, int amount = 0)
        {
            var topupOptions = await productsApi.ProductsTopupGetAsync(0, 200);
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
                if (inviter != null)
                    await TopupAmount(inviter, $"{userId}+{flag}", config["PRODUCTS:REFERAL_BONUS"], rewardSize);
            }
            refElem.Flags |= flag;
            await db.SaveChangesAsync();

            return refElem;
        }
    }
}
