using Autofac;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Miningcore.Api.Requests;
using Miningcore.Api.Responses;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Mining;
using Miningcore.Payments;
using Miningcore.Persistence;
using Miningcore.Persistence.Repositories;
using Miningcore.Util;
using NLog;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace Miningcore.Api.Controllers
{
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    [Route("api/admin")]
    [ApiController]
    public class AdminApiController : ControllerBase
    {
        public AdminApiController(IComponentContext ctx)
        {
            gcStats = ctx.Resolve<AdminGcStats>();
            clusterConfig = ctx.Resolve<ClusterConfig>();
            pools = ctx.Resolve<ConcurrentDictionary<string, IMiningPool>>();
            cf = ctx.Resolve<IConnectionFactory>();
            paymentsRepo = ctx.Resolve<IPaymentRepository>();
            balanceRepo = ctx.Resolve<IBalanceRepository>();
            payoutManager = ctx.Resolve<PayoutManager>();
        }

        private readonly ClusterConfig clusterConfig;
        private readonly IConnectionFactory cf;
        private readonly IPaymentRepository paymentsRepo;
        private readonly IBalanceRepository balanceRepo;
        private readonly ConcurrentDictionary<string, IMiningPool> pools;
        private readonly PayoutManager payoutManager;

        private AdminGcStats gcStats;
        private static readonly ILogger logger = LogManager.GetCurrentClassLogger();

        #region Actions

        [HttpGet("stats/gc")]
        public ActionResult<AdminGcStats> GetGcStats()
        {
            gcStats.GcGen0 = GC.CollectionCount(0);
            gcStats.GcGen1 = GC.CollectionCount(1);
            gcStats.GcGen2 = GC.CollectionCount(2);
            gcStats.MemAllocated = FormatUtil.FormatCapacity(GC.GetTotalMemory(false));

            return gcStats;
        }

        [HttpPost("forcegc")]
        public ActionResult<string> ForceGc()
        {
            GC.Collect(2, GCCollectionMode.Forced);
            return "Ok";
        }

        [HttpGet("pools/{poolId}/miners/{address}/getbalance")]
        public async Task<decimal> GetMinerBalanceAsync(string poolId, string address)
        {
            var balance = await cf.Run(con => balanceRepo.GetBalanceAsync(con, poolId, address));
            
            if (null != balance)
            {
                return balance.Amount;
            }
            
            return 0;
        }

        [HttpPost("pools/{poolId}/miners/{address}/forcePayout")]
        public async Task<string> ForcePayout(string poolId, string address)
        {
            logger.Info($"Forcing payout for {address}");
            try
            {
                if(string.IsNullOrEmpty(poolId))
                {
                    throw new ApiException($"Invalid pool id", HttpStatusCode.NotFound);
                }

                var pool = clusterConfig.Pools.FirstOrDefault(x => x.Id == poolId && x.Enabled);

                if(pool == null)
                {
                    throw new ApiException($"Pool {poolId} is not known", HttpStatusCode.NotFound);
                }

                return await payoutManager.PayoutSingleBalanceAsync(pool, address);
            }
            catch(Exception ex)
            {
                //rethrow as ApiException to be handled by ApiExceptionHandlingMiddleware
                throw new ApiException(ex.Message, HttpStatusCode.InternalServerError);
            }
        }

        [HttpPost("resetBalance")]
        public async Task<ResetBalanceResponse> ResetBalance(ResetBalanceRequest resetBalanceRequest)
        {
            logger.Info($"Resetting balance for {resetBalanceRequest.Address}. PoolId: {resetBalanceRequest.PoolId} Amount: {resetBalanceRequest.Address}");

            if (resetBalanceRequest.Amount <= 0)
            {
                logger.Error($"Invalid resetBalance request. Amount is less than or equal to 0 - {resetBalanceRequest.Amount}");
                throw new ApiException($"Invalid resetBalance request. Amount is less than or equal to 0 - {resetBalanceRequest.Amount}", HttpStatusCode.BadRequest);
            }

            var oldBalance = await cf.Run(con => balanceRepo.GetBalanceAsync(con, resetBalanceRequest.PoolId, resetBalanceRequest.Address));

            if (oldBalance.Amount < resetBalanceRequest.Amount)
            {
                logger.Error($"Invalid resetBalance request. Current balance is less than amount. Current balance: {oldBalance.Amount}. Amount: {resetBalanceRequest.Amount}");
                throw new ApiException($"Invalid resetBalance request. Current balance is less than amount. Current balance: {oldBalance.Amount}. Amount: {resetBalanceRequest.Amount}", HttpStatusCode.BadRequest);
            }

            await cf.Run(con => balanceRepo.AddAmountAsync(con, null, resetBalanceRequest.PoolId, resetBalanceRequest.Address, -resetBalanceRequest.Amount, "Reset balance after forced payout"));

            var newBalance = await cf.Run(con => balanceRepo.GetBalanceAsync(con, resetBalanceRequest.PoolId, resetBalanceRequest.Address));

            logger.Info($"Successfully reset balance for {resetBalanceRequest.Address}. Old Balance: {oldBalance.Amount}. New Balance: {newBalance.Amount}");

            return new ResetBalanceResponse{ OldBalance = oldBalance, NewBalance = newBalance };
        }

        #endregion // Actions
    }
}
