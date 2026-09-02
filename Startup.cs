using System;
using System.IO;
using System.Reflection;
using Coflnet.Sky.Referral.Models;
using Coflnet.Sky.Referral.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.OpenApi;
using Prometheus;
using Coflnet.Sky.Core;
using System.Net;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Coflnet.Sky.Referral
{
    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddControllers();
            services.AddSwaggerGen(c =>
            {
                c.SwaggerDoc("v1", new OpenApiInfo { Title = "SkyBase", Version = "v1" });
                // Set the comments path for the Swagger JSON and UI.
                var xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
                var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
                c.IncludeXmlComments(xmlPath);
            });

            var serverVersion = new MariaDbServerVersion(
                new Version(Configuration["MARIADB_VERSION"]));
            services.AddDbContext<ReferralDbContext>(
                dbContextOptions => dbContextOptions
                    .UseMySql(Configuration["DB_CONNECTION"], serverVersion)
                    .EnableDetailedErrors()
            );
            services.AddHostedService<BaseBackgroundService>();
            services.AddJaeger(Configuration);
            services.AddTransient<ReferralService>();
            services.AddTransient<RewardProgramService>();
            services.AddTransient<CreatorOnboardingService>();
            if (!ReferralService.IsProgramVersionConfigured(
                    Configuration["REFERRAL_PROGRAM_VERSION"]))
                throw new InvalidOperationException(
                    "REFERRAL_PROGRAM_VERSION must contain 1 to 32 characters");
            if (Configuration.GetValue<bool>("REWARDS:ENABLED")
                && (string.IsNullOrWhiteSpace(Configuration["REWARDS:WRITE_TOKEN"])
                    || Configuration["REWARDS:WRITE_TOKEN"].Length < 32
                    || string.IsNullOrWhiteSpace(Configuration["REWARDS:WRITE_ACTOR"])
                    || string.IsNullOrWhiteSpace(Configuration["REWARDS:PAYOUT_TOKEN"])
                    || Configuration["REWARDS:PAYOUT_TOKEN"].Length < 32
                    || string.IsNullOrWhiteSpace(Configuration["REWARDS:PAYOUT_ACTOR"])))
                throw new InvalidOperationException(
                    "Enabled rewards require separate writer and payout credentials");
            foreach (var token in new[]
                {
                    Configuration["CREATOR_ONBOARDING:READ_TOKEN"],
                    Configuration["CREATOR_ONBOARDING:REVIEW_TOKEN"]
                })
                if (!string.IsNullOrEmpty(token) && token.Length < 32)
                    throw new InvalidOperationException(
                        "Creator onboarding tokens must contain at least 32 characters");
            var paymentBaseUrl = Configuration["PAYMENTS_BASE_URL"];
            services.AddSingleton(col=>new Payments.Client.Api.ProductsApi(paymentBaseUrl));
            services.AddSingleton(col=>new Payments.Client.Api.UserApi(paymentBaseUrl));
            services.AddSingleton(col=>new Payments.Client.Api.TopUpApi(paymentBaseUrl));
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            app.UseExceptionHandler(errorApp =>
            {
                ErrorHandler.Add(errorApp.ApplicationServices.GetService<ILogger<Startup>>(), errorApp, "referral");
            });


            app.UseSwagger();
            app.UseSwaggerUI(c =>
            {
                c.SwaggerEndpoint("/swagger/v1/swagger.json", "SkyBase v1");
                c.RoutePrefix = "api";
            });

            app.UseHttpsRedirection();

            app.UseRouting();

            app.UseAuthorization();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapMetrics();
                endpoints.MapControllers();
            });
        }
    }
}
