using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Coflnet.Sky.Referral.Models;
using Coflnet.Sky.Referral.Services;

namespace Coflnet.Sky.Referral.Controllers
{
    /// <summary>
    /// Main Controller handling tracking
    /// </summary>
    [ApiController]
    [Route("[controller]")]
    public class ReferralController : ControllerBase
    {
        private readonly ReferralDbContext db;
        private readonly ReferralService service;

        /// <summary>
        /// Creates a new instance of <see cref="ReferralController"/>
        /// </summary>
        /// <param name="context"></param>
        /// <param name="service"></param>
        public ReferralController(ReferralDbContext context, ReferralService service)
        {
            db = context;
            this.service = service;
        }

        /// <summary>
        /// Tracks a flip
        /// </summary>
        /// <param name="userId">the user that referred someone</param>
        /// <param name="referedUser"></param>
        /// <returns></returns>
        [HttpPost]
        [Route("{userId}")]
        public async Task<ReferralElement> TrackReferral(string userId, string referedUser)
        {
            return await service.AddReferral(userId, referedUser);
        }
        /// <summary>
        /// Returns information about invited users
        /// </summary>
        /// <param name="userId">the userId to get the information for</param>
        /// <returns></returns>
        [HttpGet]
        [Route("{userId}")]
        public async Task<RefInfo> TrackReferral(string userId)
        {
            return await service.GetRefInfo(userId);
        }
    }
}
