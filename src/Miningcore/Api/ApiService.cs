/*
MiningCore 2.0
Copyright 2021 MinerNL (Miningcore.com)
*/

using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reactive.Linq;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Autofac;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc;
using Miningcore.Api.Controllers;
using Miningcore.Configuration;
using Miningcore.PoolCore;
using Miningcore.Util;
using Newtonsoft.Json;
using NLog;
using Microsoft.Extensions.Logging;
using ILogger = NLog.ILogger;
using NLog.Extensions.Logging;
using Prometheus;
using WebSocketManager;
using Miningcore.Api.Middlewares;
using Microsoft.AspNetCore.Http;
using AspNetCoreRateLimit;
using Microsoft.IdentityModel.Tokens;

namespace Miningcore.Api
{
    internal class ApiService
    {
        private static readonly ILogger logger = LogManager.GetLogger("Api");
        private static IWebHost webHost;
        private static readonly IPAddress IPv4LoopBackOnIPv6 = IPAddress.Parse("::ffff:127.0.0.1");

        internal static void StartApiService(ClusterConfig clusterConfig)
        {
            var address = clusterConfig.Api?.ListenAddress != null ? (clusterConfig.Api.ListenAddress != "*" ? IPAddress.Parse(clusterConfig.Api.ListenAddress) : IPAddress.Any) : IPAddress.Parse("127.0.0.1");
            var port = clusterConfig.Api?.Port ?? 4000;
            var enableApiRateLimiting = (clusterConfig.Api?.RateLimiting?.Enabled == true);

            logger.Info(() => $"Starting API Service @ {address}:{port} {(enableApiRateLimiting ? " [rate-limiting enabled]" : " [rate-limiting disabled]" )}");

            webHost = WebHost.CreateDefaultBuilder()
                .ConfigureLogging(logging =>
                {
                    // NLog
                    logging.ClearProviders();
                    logging.AddNLog();

                    logging.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Trace);
                })
                .ConfigureServices(services =>
                {
                    // Memory Cache
                    services.AddMemoryCache();

                    // rate limiting
                    if(enableApiRateLimiting)
                    {

                        services.Configure<IpRateLimitOptions>(ConfigureIpRateLimitOptions);
                        services.AddSingleton<IIpPolicyStore, MemoryCacheIpPolicyStore>();
                        services.AddSingleton<IRateLimitCounterStore, MemoryCacheRateLimitCounterStore>();
                        services.AddSingleton<IRateLimitConfiguration, RateLimitConfiguration>();
                    }

                    // Controllers
                    services.AddSingleton<PoolApiController, PoolApiController>();
                    services.AddSingleton<AdminApiController, AdminApiController>();

                    // MVC
                    services.AddSingleton((IComponentContext) Pool.container);
                    services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();

                    services.AddTransient<WebSocketNotificationsRelay>();

					// ToDo: Setting check
					//services.AddMvc()
                    //    .SetCompatibilityVersion(CompatibilityVersion.Version_3_0)
                    //    .AddControllersAsServices()
                    //    .AddNewtonsoftJson(options =>
                    //    {
                    //        options.SerializerSettings.Formatting = Formatting.Indented;
                    //    });
                    //services.AddMvc(option => option.EnableEndpointRouting = false);




#if NETCOREAPP2_1
                    services.AddMvc()
                        .SetCompatibilityVersion(CompatibilityVersion.Version_2_1)
                        .AddControllersAsServices()
                        .AddJsonOptions(options =>
                        {
                            options.SerializerSettings.Formatting = Formatting.Indented;
                        });
#elif NETCOREAPP3_1
                    services.AddControllers()
                        .SetCompatibilityVersion(CompatibilityVersion.Version_3_0)
                        .AddControllersAsServices()
                        .AddNewtonsoftJson(options =>
                        {
                            options.SerializerSettings.Formatting = Formatting.Indented;
                        });
#else
                   services.AddControllers()
                        .SetCompatibilityVersion(CompatibilityVersion.Latest)
                        .AddControllersAsServices()
                        .AddNewtonsoftJson(options =>
                        {
                            options.SerializerSettings.Formatting = Formatting.Indented;
                        });


#endif
                    // .ContractResolver = new DefaultContractResolver());

                    services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                            .AddJwtBearer(options =>
                            {
                                options.Authority = clusterConfig.Api.OidcValidIssuer;
                                options.Audience = clusterConfig.Api.OidcValidateAudience ? clusterConfig.Api.OidcValidAudience : null;
                                options.MetadataAddress = clusterConfig.Api.OidcMetadataAddress;
                                options.RequireHttpsMetadata = true;
                                options.TokenValidationParameters = new TokenValidationParameters
                                {
                                    ValidateIssuer = true,
                                    ValidateLifetime = true,
                                    ValidateIssuerSigningKey = true,
                                    ValidateAudience = clusterConfig.Api.OidcValidateAudience,
                                };
                            });

                    // Gzip Compression
                    services.AddResponseCompression();

                    // Cors
                    // ToDo: Test if Admin portal is working without .credentials()
                    // .AllowAnyOrigin(_ => true)
                    // .AllowCredentials()
                    services.AddCors(options =>
                    {
                        options.AddPolicy("CorsPolicy",
                            builder => builder.AllowAnyOrigin()
                                              .AllowAnyMethod()
                                              .AllowAnyHeader()
                                          );
                    }
                    );
					// ToDo: AddCors to check:   .WithExposedHeaders("x-total-count")
					
					
                    // WebSockets
                    services.AddWebSocketManager();
                })
                .Configure(app =>
                {
                    if(enableApiRateLimiting)
                        app.UseIpRateLimiting();

                    app.UseMiddleware<ApiExceptionHandlingMiddleware>();

                    UseIpWhiteList(app, true, new[] { "/metrics" }, clusterConfig.Api?.MetricsIpWhitelist);

                    app.UseResponseCompression();
                    app.UseCors("CorsPolicy");
                    app.UseWebSockets();
                    app.MapWebSocketManager("/notifications", app.ApplicationServices.GetService<WebSocketNotificationsRelay>());
                    app.UseMetricServer();

#if NETCOREAPP2_1
                    app.UseMvc();
#else
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => {
                        endpoints.MapDefaultControllerRoute();
                        endpoints.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");
                    });
#endif

                })
                .UseKestrel(options =>
                {
                    options.Listen(address, clusterConfig.Api.Port, listenOptions =>
                    {
                        if(clusterConfig.Api.SSLConfig?.Enabled == true)
                            listenOptions.UseHttps(clusterConfig.Api.SSLConfig.SSLPath, clusterConfig.Api.SSLConfig.SSLPassword);
                    });
                })
                .Build();

            webHost.Start();

            logger.Info(() => $"API Online @ {address}:{port} { (!enableApiRateLimiting ? " [rate-limiting disabled]" : string.Empty) }");
            logger.Info(() => $"Prometheus Metrics Online @ {address}:{port}/metrics");
            logger.Info(() => $"WebSocket notifications streaming @ {address}:{port}/notifications");
        }

        private static void ConfigureIpRateLimitOptions(IpRateLimitOptions options)
        {
            options.EnableEndpointRateLimiting = false;

            // exclude admin api and metrics from throtteling
            options.EndpointWhitelist = new List<string>
            {
                "*:/api/admin",
                "get:/metrics",
                "*:/notifications",
            };

            options.IpWhitelist = Pool.clusterConfig.Api?.RateLimiting?.IpWhitelist?.ToList();

            // default to whitelist localhost if whitelist absent
            if(options.IpWhitelist == null || options.IpWhitelist.Count == 0)
            {
                options.IpWhitelist = new List<string>
                {
                    IPAddress.Loopback.ToString(),
                    IPAddress.IPv6Loopback.ToString(),
                    IPUtils.IPv4LoopBackOnIPv6.ToString()
                };
            }

            // limits
            var rules = Pool.clusterConfig.Api?.RateLimiting?.Rules?.ToList();

            if(rules == null || rules.Count == 0)
            {
                rules = new List<RateLimitRule>
                {
                    new RateLimitRule
                    {
                        Endpoint = "*",
                        Period = "1s",
                        Limit = 5,
                    }
                };
            }

            options.GeneralRules = rules;

            logger.Info(() => $"API access limited to {(string.Join(", ", rules.Select(x => $"{x.Limit} requests per {x.Period}")))}, except from {string.Join(", ", options.IpWhitelist)}");
        }

        private static void UseIpWhiteList(IApplicationBuilder app, bool defaultToLoopback, string[] locations, string[] whitelist)
        {
            var ipList = whitelist?.Select(x => IPAddress.Parse(x)).ToList();
            if(defaultToLoopback && (ipList == null || ipList.Count == 0))
                ipList = new List<IPAddress>(new[] { IPAddress.Loopback, IPAddress.IPv6Loopback, IPUtils.IPv4LoopBackOnIPv6 });

            if(ipList.Count > 0)
            {
                // always allow access by localhost
                if(!ipList.Any(x => x.Equals(IPAddress.Loopback)))
                    ipList.Add(IPAddress.Loopback);
                if(!ipList.Any(x => x.Equals(IPAddress.IPv6Loopback)))
                    ipList.Add(IPAddress.IPv6Loopback);
                if(!ipList.Any(x => x.Equals(IPUtils.IPv4LoopBackOnIPv6)))
                    ipList.Add(IPUtils.IPv4LoopBackOnIPv6);

                logger.Info(() => $"API Access to {string.Join(",", locations)} restricted to {string.Join(",", ipList.Select(x => x.ToString()))}");

                app.UseMiddleware<IPAccessWhitelistMiddleware>(locations, ipList.ToArray());
            }
        }

    }
}
