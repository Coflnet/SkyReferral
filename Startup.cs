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
using Microsoft.OpenApi.Models;
using Prometheus;
using Coflnet.Sky.Core;
using System.Net;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
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

            // Replace with your server version and type.
            // Use 'MariaDbServerVersion' for MariaDB.
            // Alternatively, use 'ServerVersion.AutoDetect(connectionString)'.
            // For common usages, see pull request #1233.
            var serverVersion = new MariaDbServerVersion(new Version(Configuration["MARIADB_VERSION"]));

            // Replace 'YourDbContext' with the name of your own DbContext derived class.
            services.AddDbContext<ReferralDbContext>(
                dbContextOptions => dbContextOptions
                    .UseMySql(Configuration["DB_CONNECTION"], serverVersion)
                    .EnableSensitiveDataLogging() // <-- These two calls are optional but help
                    .EnableDetailedErrors()       // <-- with debugging (remove for production).
            );
            services.AddHostedService<BaseBackgroundService>();
            services.AddJaeger();
            services.AddTransient<ReferralService>();
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
            app.UseSwagger();
            app.UseSwaggerUI(c =>
            {
                c.SwaggerEndpoint("/swagger/v1/swagger.json", "SkyBase v1");
                c.RoutePrefix = "api";
            });

            app.UseHttpsRedirection();

            app.UseRouting();

            app.UseAuthorization();

            app.UseExceptionHandler(errorApp =>
            {
                errorApp.Run(async context =>
                {
                    context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                    context.Response.ContentType = "text/json";

                    var exceptionHandlerPathFeature =
                        context.Features.Get<IExceptionHandlerPathFeature>();

                    if (exceptionHandlerPathFeature?.Error is ApiException ex)
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                        await context.Response.WriteAsync(
                                        JsonConvert.SerializeObject(new { ex.Message }));
                       // badRequestCount.Inc();
                    }
                    else
                    {
                        using var span = OpenTracing.Util.GlobalTracer.Instance.BuildSpan("error").StartActive();
                        span.Span.Log(exceptionHandlerPathFeature?.Error?.Message);
                        span.Span.Log(exceptionHandlerPathFeature?.Error?.StackTrace);
                        var traceId = System.Net.Dns.GetHostName().Replace("commands", "").Trim('-') + "." + span.Span.Context.TraceId;
                        dev.Logger.Instance.Error(exceptionHandlerPathFeature?.Error, "fatal request error " + span.Span.Context.TraceId);
                        await context.Response.WriteAsync(
                            JsonConvert.SerializeObject(new
                            {
                                slug = "internal_error",
                                message = "An unexpected internal error occured. Please check that your request is valid. If it is please report he error and include the Trace.",
                                trace = traceId
                            }));
                        //errorCount.Inc();
                    }
                });
            });

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapMetrics();
                endpoints.MapControllers();
            });
        }
    }
}
