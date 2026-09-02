using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Coflnet.Sky.Referral.Models;
using Coflnet.Sky.Referral.Services;
using Microsoft.Extensions.Configuration;

namespace Coflnet.Sky.Referral.Controllers
{
    /// <summary>
    /// Main Controller handling tracking
    /// </summary>
    [ApiController]
    [Route("[controller]")]
    public class ReferralController : ControllerBase
    {
        private readonly ReferralService service;
        private readonly IConfiguration configuration;

        /// <summary>
        /// Creates a new instance of <see cref="ReferralController"/>
        /// </summary>
        /// <param name="service"></param>
        /// <param name="configuration"></param>
        public ReferralController(
            ReferralService service,
            IConfiguration configuration)
        {
            this.service = service;
            this.configuration = configuration;
        }

        /// <summary>
        /// Tracks a flip
        /// </summary>
        /// <param name="userId">the user that referred someone</param>
        /// <param name="referedUser"></param>
        /// <param name="programVersion">offer version displayed before sign-in</param>
        /// <param name="locale">locale in which the offer was displayed</param>
        /// <returns></returns>
        [HttpPost]
        [Route("{userId}")]
        public async Task<ReferralElement> TrackReferral(
            string userId,
            string referedUser,
            string programVersion,
            string locale)
        {
            var expected = configuration["REFERRAL_MUTATION_TOKEN"];
            var supplied = Request.Headers["X-Referral-Mutation-Token"].ToString();
            if (expected?.Length < 32 || supplied.Length < 32
                || !CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(Encoding.UTF8.GetBytes(expected)),
                    SHA256.HashData(Encoding.UTF8.GetBytes(supplied))))
                throw new ApiException("Referral mutation is not authorized");
            return await service.AddReferral(userId, referedUser, programVersion, locale);
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
