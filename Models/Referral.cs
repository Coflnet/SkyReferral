
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
        /// Legacy first-purchase amount retained for existing rows; current
        /// referral processing does not write it.
        /// </summary>
        public int PurchaseAmount { get; set; }
        [DataMember(Name = "flags")]
        public ReferralFlags Flags { get; set; }
        [DataMember(Name = "programVersion")]
        [MaxLength(32)]
        public string ProgramVersion { get; set; }
        [DataMember(Name = "locale")]
        [MaxLength(35)]
        public string Locale { get; set; }
        [DataMember(Name = "updatedAt")]
        public DateTime UpdatedAt { get; set; }
        [DataMember(Name = "createdAt")]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
