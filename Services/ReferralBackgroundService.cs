using System.Threading;
using System.Threading.Tasks;
using Coflnet.Sky.Referral.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Confluent.Kafka;
using Microsoft.Extensions.Configuration;
using System;
using Microsoft.Extensions.Logging;
using Coflnet.Sky.Referral.Controllers;
using Coflnet.Payments.Client.Model;
using Coflnet.Payments.Client.Api;
using System.Runtime.Serialization;

namespace Coflnet.Sky.Referral.Services
{

    public class BaseBackgroundService : BackgroundService
    {
        private IServiceScopeFactory scopeFactory;
        private IConfiguration config;
        private ILogger<BaseBackgroundService> logger;

        public BaseBackgroundService(
            IServiceScopeFactory scopeFactory, IConfiguration config, ILogger<BaseBackgroundService> logger)
        {
            this.scopeFactory = scopeFactory;
            this.config = config;
            this.logger = logger;
        }
        /// <summary>
        /// Called by asp.net on startup
        /// </summary>
        /// <param name="stoppingToken">is canceled when the applications stops</param>
        /// <returns></returns>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await Migrate();

            var flipCons = Kafka.KafkaConsumer.Consume<TransactionEvent>(config, config["TOPICS:TRANSACTION"], async lp =>
            {
                using var scope = scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<ReferralService>();
                await service.NewPurchase(lp.UserId, lp.Amount, lp.Reference, lp.ProductSlug);
            }, stoppingToken, "sky-referral", AutoOffsetReset.Earliest, new TransactionDeserializer());
            var verfify = Kafka.KafkaConsumer.Consume<VerificationEvent>(config, config["TOPICS:VERIFIED"], async lp =>
            {
                using var scope = scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<ReferralService>();
                await service.Verified(lp.UserId, lp.MinecraftUuid, lp.ExistingConCount);
            }, stoppingToken, "sky-referral");

            await Task.WhenAny(flipCons, verfify);
            throw new Exception("consuming ended");
        }

        private async Task Migrate()
        {
            logger.LogInformation("applying pending migrations");
            var scope = scopeFactory.CreateScope();
            using var context = scope.ServiceProvider.GetRequiredService<ReferralDbContext>();
            // make sure all migrations are applied and block until they are
            context.Database.MigrateAsync().Wait();
            logger.LogInformation("applied pending migrations");
            await Task.Yield();
        }


        [DataContract]
        public class VerificationEvent
        {
            /// <summary>
            /// UserId of the user
            /// </summary>
            /// <value></value>
            [DataMember(Name = "userId")]
            public string UserId { get; set; }
            /// <summary>
            /// Minecraft uuid of the verified account
            /// </summary>
            /// <value></value>
            [DataMember(Name = "uuid")]
            public string MinecraftUuid { get; set; }
            /// <summary>
            /// How many existing verifications are on this minecraft account
            /// </summary>
            [DataMember(Name = "existing")]
            public int ExistingConCount { get; set; }
        }

        public class TransactionDeserializer : IDeserializer<Payments.Client.Model.TransactionEvent>
        {
            public Payments.Client.Model.TransactionEvent Deserialize(ReadOnlySpan<byte> data, bool isNull, SerializationContext context)
            {
                return Newtonsoft.Json.JsonConvert.DeserializeObject<Payments.Client.Model.TransactionEvent>(System.Text.Encoding.UTF8.GetString(data));
            }
        }
    }
}