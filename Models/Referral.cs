
using System;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using System.ComponentModel.DataAnnotations;

namespace Coflnet.Sky.Referral.Models
{
    [DataContract]
    public class ReferralElement
    {
        [IgnoreDataMember]
        [JsonIgnore]
        public int Id { get; set; }
        [DataMember(Name = "inviter")]
        [MaxLength(32)]
        public string Inviter { get; set; }
        [DataMember(Name = "invited")]
        [MaxLength(32)]
        public string Invited { get; set; }
        /// <summary>
        /// How many coins were purchased by the invited user (first purchase)
        /// </summary>
        public int PurchaseAmount { get; set; }
        [DataMember(Name = "flags")]
        public ReferralFlags Flags { get; set; }
        [Timestamp]
        [DataMember(Name = "updatedAt")]
        public DateTime UpdatedAt { get; set; }
        [Timestamp]
        [DataMember(Name = "createdAt")]
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }
}