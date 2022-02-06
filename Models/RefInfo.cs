using System.Collections.Generic;

namespace Coflnet.Sky.Referral.Models
{
    public class RefInfo
    {
        public ReferralElement Inviter { get; set; }
        public List<ReferralElement> Invited { get; set; }
    }
}