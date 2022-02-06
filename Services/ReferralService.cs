using System.Threading.Tasks;
using Coflnet.Sky.Referral.Models;
using System;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Coflnet.Payments.Client.Api;
using System.Collections.Generic;

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

        public ReferralService(ReferralDbContext db, TopUpApi topUpApi, UserApi paymentUserApi, ProductsApi productsApi)
        {
            this.db = db;
            this.topUpApi = topUpApi;
            this.paymentUserApi = paymentUserApi;
            this.productsApi = productsApi;
        }

        public async Task<ReferralElement> AddReferral(string userId, string referredUser)
        {
            var flipFromDb = await db.Referrals.Where(f => f.Invited == referredUser).FirstOrDefaultAsync();
            if (flipFromDb != null)
                throw new ApiException("You have already used a referral link");

            var flip = new ReferralElement() { Inviter = userId, Invited = referredUser };
            db.Referrals.Add(flip);
            await db.SaveChangesAsync();
            return flip;
        }

        public async Task<RefInfo> GetRefInfo(string userId)
        {
            var refedByTask = db.Referrals.Where(r => r.Invited == userId).FirstOrDefaultAsync();
            var referrals = await db.Referrals.Where(r => r.Inviter == userId).ToListAsync();
            var refedBy = await refedByTask;
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
        /// <returns></returns>
        public async Task NewPurchase(string userId, double size, string reference)
        {
            var user = await GetUserAndAwardBonusToInviter(userId, ReferralFlags.FIRST_PURCHASE_BONUS, rewardSize: (int)size);
            // nothing more todo :) (maybe give extra bonus to new user in the future)
        }


        public async Task Verified(string userId, string minecraftUuid)
        {
            var user = await GetUserAndAwardBonusToInviter(userId, ReferralFlags.VERIFIED_MC_ACCOUNT, rewardSize: 100);
            var id = user.Invited;
            // give user 24 hours of special premium
            // todo
            var optionName = "verify-mc";
            var amount = 0;
            await TopupAmount(userId, minecraftUuid, optionName, amount);
            var productName = "test-premium";
            paymentUserApi.UserUserIdPurchaseProductSlugPost(userId, productName);
        }

        private async Task TopupAmount(string userId, string reference, string optionName, int amount = 0)
        {
            var topupOptions = await productsApi.ProductsTopupGetAsync(0, 200);
            var topupInvite = topupOptions.Where(t => t.Slug == optionName).FirstOrDefault();
            if (topupInvite == null)
                throw new ApiException($"Custom topuOption {optionName} doesn't exist");
            await topUpApi.TopUpCustomPostAsync(userId, new Payments.Client.Model.CustomTopUp()
            {
                ProductId = topupInvite.Slug,
                Reference = reference,
                Amount = amount
            });
        }

        private async Task<ReferralElement> GetUserAndAwardBonusToInviter(string userId, ReferralFlags flag, int rewardSize)
        {
            var user = await db.Referrals.Where(r => r.Invited == userId).FirstOrDefaultAsync();
            if (!user.Flags.HasFlag(flag))
            {
                user.Flags |= flag;
                // award coins to inviter
                var inviter = user.Inviter;
                await TopupAmount(inviter, $"{userId}+{flag}", "referal-bonus", rewardSize);
            }

            return user;
        }
    }
}
