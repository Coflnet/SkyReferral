using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using System;
using System.Runtime.Serialization;
using Coflnet.Payments.Client.Model;
using Confluent.Kafka;

namespace Coflnet.Sky.Referral.Services
{

    public class BaseBackgroundService : BackgroundService
    {
        private IServiceScopeFactory scopeFactory;
        private IConfiguration config;

        public BaseBackgroundService(
            IServiceScopeFactory scopeFactory, IConfiguration config)
        {
            this.scopeFactory = scopeFactory;
            this.config = config;
        }
        /// <summary>
        /// Called by asp.net on startup
        /// </summary>
        /// <param name="stoppingToken">is canceled when the applications stops</param>
        /// <returns></returns>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var transactions = Kafka.KafkaConsumer.Consume<TransactionEvent>(
                config,
                config["TOPICS:TRANSACTION"],
                async transaction =>
                {
                    const string prefix = "revert transaction ";
                    if (transaction.ProductSlug != "revert"
                        || transaction.Reference?.StartsWith(prefix, StringComparison.Ordinal) != true
                        || !long.TryParse(transaction.Reference[prefix.Length..], out var purchaseId))
                        return;
                    using var scope = scopeFactory.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<RewardProgramService>()
                        .ReverseCreatorFee(purchaseId);
                },
                stoppingToken,
                "sky-referral-rewards",
                AutoOffsetReset.Earliest,
                new TransactionDeserializer());
            var verfify = Kafka.KafkaConsumer.Consume<VerificationEvent>(config, config["TOPICS:VERIFIED"], async lp =>
            {
                using var scope = scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<ReferralService>();
                await service.Verified(lp.UserId, lp.MinecraftUuid, lp.ExistingConCount);
            }, stoppingToken, "sky-referral");

            await Task.WhenAny(transactions, verfify);
            throw new Exception("consuming ended");
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

        private sealed class TransactionDeserializer : IDeserializer<TransactionEvent>
        {
            public TransactionEvent Deserialize(
                ReadOnlySpan<byte> data,
                bool isNull,
                SerializationContext context) =>
                Newtonsoft.Json.JsonConvert.DeserializeObject<TransactionEvent>(
                    System.Text.Encoding.UTF8.GetString(data));
        }
    }
}
