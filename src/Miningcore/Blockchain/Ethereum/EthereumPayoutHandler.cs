using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using AutoMapper;
using Microsoft.Extensions.Caching.Memory;
using Miningcore.Blockchain.Ethereum.Configuration;
using Miningcore.Blockchain.Ethereum.DaemonRequests;
using Miningcore.Configuration;
using Miningcore.DaemonInterface;
using Miningcore.DataStore.Cloud.EtherScan;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.Notifications.Messages;
using Miningcore.Payments;
using Miningcore.Persistence;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Repositories;
using Miningcore.Time;
using Miningcore.Util;
using Nethereum.Web3;
using Nethereum.Web3.Accounts;
using Newtonsoft.Json;
using Block = Miningcore.Persistence.Model.Block;
using Contract = Miningcore.Contracts.Contract;

namespace Miningcore.Blockchain.Ethereum
{
    [CoinFamily(CoinFamily.Ethereum)]
    public class EthereumPayoutHandler : PayoutHandlerBase, IPayoutHandler
    {
        public EthereumPayoutHandler(
            IComponentContext ctx,
            IConnectionFactory cf,
            IMapper mapper,
            IShareRepository shareRepo,
            IStatsRepository statsRepo,
            IBlockRepository blockRepo,
            IBalanceRepository balanceRepo,
            IPaymentRepository paymentRepo,
            IMasterClock clock,
            IMessageBus messageBus) :
            base(cf, mapper, shareRepo, blockRepo, balanceRepo, paymentRepo, clock, messageBus)
        {
            Contract.RequiresNonNull(ctx, nameof(ctx));
            Contract.RequiresNonNull(cf, nameof(cf));
            Contract.RequiresNonNull(mapper, nameof(mapper));
            Contract.RequiresNonNull(shareRepo, nameof(shareRepo));
            Contract.RequiresNonNull(balanceRepo, nameof(balanceRepo));
            Contract.RequiresNonNull(blockRepo, nameof(blockRepo));
            Contract.RequiresNonNull(paymentRepo, nameof(paymentRepo));
            Contract.RequiresNonNull(statsRepo, nameof(statsRepo));

            this.ctx = ctx;
            this.statsRepo = statsRepo;
        }

        private readonly IStatsRepository statsRepo;
        private readonly IComponentContext ctx;
        private IDaemonClient daemon;
        private EthereumNetworkType networkType;
        private ParityChainType chainType;
        private BigInteger chainId;
        private const int BlockSearchOffset = 50;
        private EthereumPoolConfigExtra extraPoolConfig;
        private EthereumPoolPaymentProcessingConfigExtra extraConfig;
        private DaemonEndpointConfig daemonEndpointConfig;
        private Task ondemandPayTask;
        private IWeb3 web3Connection;
        private bool isParity = true;
        private const int TwentyFourHrs = 24;
        private const string BlockReward = "blockReward";
        private const string BlockAvgTime = "blockAvgTime";
        private const string PoolBlockAvgTime = "poolBlockAvgTime";
        private const decimal DefaultRecipientShare = 0.85m;
        private const float Sixty = 60; // The most important constant defined in this program
        private const decimal MaxPayout = 0.1m; // ethereum
        private const double DefaultMaxBlockReward = 0.1d; //ethereum
        private const int DefaultPayoutInterval = 600;
        private static ConcurrentDictionary<string, string> transactionHashes = new();

        protected override string LogCategory => "Ethereum Payout Handler";

        #region For Testing

        public void ConfigureAsync(ClusterConfig clusterCfg, PoolConfig poolCfg, IDaemonClient daemonClient, IWeb3 web3)
        {
            poolConfig = poolCfg;
            clusterConfig = clusterCfg;
            extraPoolConfig = poolConfig.Extra.SafeExtensionDataAs<EthereumPoolConfigExtra>();
            extraConfig = poolConfig.PaymentProcessing.Extra.SafeExtensionDataAs<EthereumPoolPaymentProcessingConfigExtra>();
            logger = LogUtil.GetPoolScopedLogger(typeof(EthereumPayoutHandler), poolConfig);
            daemon = daemonClient;
            web3Connection = web3;
        }

        #endregion

        #region IPayoutHandler

        public async Task ConfigureAsync(ClusterConfig clusterCfg, PoolConfig poolCfg)
        {
            poolConfig = poolCfg;
            clusterConfig = clusterCfg;
            extraPoolConfig = poolConfig.Extra.SafeExtensionDataAs<EthereumPoolConfigExtra>();
            extraConfig = poolConfig.PaymentProcessing.Extra.SafeExtensionDataAs<EthereumPoolPaymentProcessingConfigExtra>();

            logger = LogUtil.GetPoolScopedLogger(typeof(EthereumPayoutHandler), poolConfig);

            // configure standard daemon
            var jsonSerializerSettings = ctx.Resolve<JsonSerializerSettings>();

            var daemonEndpoints = poolConfig.Daemons.Where(x => string.IsNullOrEmpty(x.Category)).ToArray();
            daemon = new DaemonClient(jsonSerializerSettings, messageBus, clusterConfig?.ClusterName ?? poolConfig.PoolName, poolConfig.Id);
            daemon.Configure(daemonEndpoints);
            daemonEndpointConfig = daemonEndpoints.First();

            await DetectChainAsync();

            // if pKey is configured - setup web3 connection for self managed wallet payouts
            InitializeWeb3(daemonEndpointConfig);
        }

        public async Task<Block[]> ClassifyBlocksAsync(Block[] blocks)
        {
            Contract.RequiresNonNull(poolConfig, nameof(poolConfig));
            Contract.RequiresNonNull(blocks, nameof(blocks));

            var coin = poolConfig.Template.As<EthereumCoinTemplate>();
            var pageSize = 100;
            var pageCount = (int) Math.Ceiling(blocks.Length / (double) pageSize);
            var blockCache = new Dictionary<long, DaemonResponses.Block>();
            var result = new List<Block>();
            for(var i = 0; i < pageCount; i++)
            {
                // get a page full of blocks
                var page = blocks
                    .Skip(i * pageSize)
                    .Take(pageSize)
                    .ToArray();

                // get latest block
                var latestBlockResponses = await daemon.ExecuteCmdAllAsync<DaemonResponses.Block>(logger, EthCommands.GetBlockByNumber, new[] { (object) "latest", true });
                var latestBlockHeight = latestBlockResponses.First(x => x.Error == null && x.Response?.Height != null).Response.Height.Value;

                // execute batch
                var blockInfos = await FetchBlocks(blockCache, page.Select(block => (long) block.BlockHeight).ToArray());

                for(var j = 0; j < blockInfos.Length; j++)
                {
                    logger.Info(() => $"Blocks count to process : {blockInfos.Length - j}");
                    var blockInfo = blockInfos[j];
                    var block = page[j];

                    // extract confirmation data from stored block
                    var mixHash = block.TransactionConfirmationData.Split(":").First();
                    var nonce = block.TransactionConfirmationData.Split(":").Last();
                    logger.Debug(() => $"** TransactionData: {block.TransactionConfirmationData}");


                    // update progress
                    block.ConfirmationProgress = Math.Min(1.0d, (double) (latestBlockHeight - block.BlockHeight) / EthereumConstants.MinConfimations);
                    result.Add(block);

                    messageBus.NotifyBlockConfirmationProgress(poolConfig.Id, block, coin);


                    if(string.Equals(blockInfo.Miner, poolConfig.Address, StringComparison.OrdinalIgnoreCase))
                    {
                        // additional check
                        // NOTE: removal of first character of both sealfields caused by
                        // https://github.com/paritytech/parity/issues/1090
                        logger.Info(() => $"** Ethereum Deamon is Parity : {isParity}");

                        // is the block mined by us?
                        bool match = false;
                        if(isParity)
                        {
                            match = isParity ? true : blockInfo.SealFields[0].Substring(2) == mixHash && blockInfo.SealFields[1].Substring(2) == nonce;
                            logger.Debug(() => $"** Parity mixHash : {blockInfo.SealFields[0].Substring(2)} =?= {mixHash}");
                            logger.Debug(() => $"** Parity nonce   : {blockInfo.SealFields[1].Substring(2)} =?= {nonce}");
                        }
                        else
                        {
                            if(blockInfo.MixHash == mixHash && blockInfo.Nonce == nonce)
                            {
                                match = true;
                                logger.Debug(() => $"** Geth mixHash : {blockInfo.MixHash} =?= {mixHash}");
                                logger.Debug(() => $"** Geth nonce   : {blockInfo.Nonce} =?= {nonce}");
                                logger.Debug(() => $"** (MIXHASH_NONCE) Is the Block mined by us? {match}");
                            }

                            if(blockInfo.Miner == poolConfig.Address)
                            {
                                //match = true;
                                logger.Debug(() => $"Is the block mined by us? Yes if equal: {blockInfo.Miner} =?= {poolConfig.Address}");
                                logger.Debug(() => $"** (WALLET_MATCH) Is the Block mined by us? {match}");
                                logger.Debug(() => $"** Possible Uncle or Orphan block found");
                            }
                        }

                        // mature?
                        if(match && (latestBlockHeight - block.BlockHeight >= EthereumConstants.MinConfimations))
                        {
                            block.Status = BlockStatus.Confirmed;
                            block.ConfirmationProgress = 1;
                            block.BlockHeight = (ulong) blockInfo.Height;
                            block.Reward = GetBaseBlockReward(chainType, block.BlockHeight); // base reward
                            block.Type = "block";

                            if(extraConfig?.KeepUncles == false)
                                block.Reward += blockInfo.Uncles.Length * (block.Reward / 32); // uncle rewards

                            if(extraConfig?.KeepTransactionFees == false && blockInfo.Transactions?.Length > 0)
                                block.Reward += await GetTxRewardAsync(blockInfo); // tx fees

                            var gasFeeInEth = Web3.Convert.FromWei(blockInfo.BaseFeePerGas);
                            var burntFee = gasFeeInEth * blockInfo.GasUsed; // burnt fees
                            block.Reward = block.Reward - burntFee;
                            logger.Info(() => $"[{LogCategory}] Unlocked block {block.BlockHeight} worth {FormatAmount(block.Reward)}");

                            messageBus.NotifyBlockUnlocked(poolConfig.Id, block, coin);
                        }

                        continue;
                    }

                    // search for a block containing our block as an uncle by checking N blocks in either direction
                    var heightMin = block.BlockHeight - BlockSearchOffset;
                    var heightMax = Math.Min(block.BlockHeight + BlockSearchOffset, latestBlockHeight);
                    var range = new List<long>();

                    for(var k = heightMin; k < heightMax; k++)
                        range.Add((long) k);

                    // execute batch
                    var blockInfo2s = await FetchBlocks(blockCache, range.ToArray());

                    foreach(var blockInfo2 in blockInfo2s)
                    {
                        // don't give up yet, there might be an uncle
                        if(blockInfo2.Uncles.Length > 0)
                        {
                            // fetch all uncles in a single RPC batch request
                            var uncleBatch = blockInfo2.Uncles.Select((x, index) => new DaemonCmd(EthCommands.GetUncleByBlockNumberAndIndex,
                                new[] { blockInfo2.Height.Value.ToStringHexWithPrefix(), index.ToStringHexWithPrefix() })).ToArray();

                            logger.Info(() => $"[{LogCategory}] Fetching {blockInfo2.Uncles.Length} uncles for block {blockInfo2.Height}");

                            var uncleResponses = await daemon.ExecuteBatchAnyAsync(logger, uncleBatch);

                            logger.Info(() =>
                                $"[{LogCategory}] Fetched {uncleResponses.Count(x => x.Error == null && x.Response != null)} uncles for block {blockInfo2.Height}");

                            var uncle = uncleResponses.Where(x => x.Error == null && x.Response != null)
                                .Select(x => x.Response.ToObject<DaemonResponses.Block>())
                                .FirstOrDefault(x => string.Equals(x.Miner, poolConfig.Address, StringComparison.OrdinalIgnoreCase));

                            if(uncle != null)
                            {
                                // mature?
                                if(latestBlockHeight - uncle.Height.Value >= EthereumConstants.MinConfimations)
                                {
                                    block.Status = BlockStatus.Confirmed;
                                    block.ConfirmationProgress = 1;
                                    block.Reward = GetUncleReward(chainType, uncle.Height.Value, blockInfo2.Height.Value);
                                    block.BlockHeight = uncle.Height.Value;
                                    block.Type = EthereumConstants.BlockTypeUncle;

                                    logger.Info(() =>
                                        $"[{LogCategory}] Unlocked uncle for block {blockInfo2.Height.Value} at height {uncle.Height.Value} worth {FormatAmount(block.Reward)}");

                                    messageBus.NotifyBlockUnlocked(poolConfig.Id, block, coin);
                                }

                                else
                                    logger.Info(() => $"[{LogCategory}] Got immature matching uncle for block {blockInfo2.Height.Value}. Will try again.");

                                break;
                            }
                        }
                    }

                    if(block.Status == BlockStatus.Pending && block.ConfirmationProgress > 0.75)
                    {
                        // we've lost this one
                        block.Status = BlockStatus.Orphaned;
                        block.Reward = 0;

                        messageBus.NotifyBlockUnlocked(poolConfig.Id, block, coin);
                    }
                }
            }

            foreach(var block in result)
            {
                TelemetryUtil.TrackEvent("BlockFound_" + poolConfig.Id, new Dictionary<string, string>
                {
                    {"miner", block.Miner},
                    {"height", block.BlockHeight.ToString()},
                    {"type", block.Type},
                    {"reward", FormatAmount(block.Reward)}
                });
            }

            return result.ToArray();
        }

        public Task CalculateBlockEffortAsync(Block block, double accumulatedBlockShareDiff)
        {
            block.Effort = accumulatedBlockShareDiff / block.NetworkDifficulty;

            return Task.FromResult(true);
        }

        public override async Task<decimal> UpdateBlockRewardBalancesAsync(IDbConnection con, IDbTransaction tx, Block block, PoolConfig pool)
        {
            // Despite of whether block found or not always calculate rewards based on ether scan api
            return await CalculateBlockData(pool);
        }

        public async Task PayoutAsync(Balance[] balances, CancellationToken ct)
        {
            logger.Info(() => $"[{LogCategory}] Beginning payout to {balances?.Length} miners.");

            // ensure we have peers
            var infoResponse = await daemon.ExecuteCmdSingleAsync<string>(logger, EthCommands.GetPeerCount);

            if(networkType == EthereumNetworkType.Main && (infoResponse.Error != null || string.IsNullOrEmpty(infoResponse.Response) || infoResponse.Response.IntegralFromHex<int>() < EthereumConstants.MinPayoutPeerCount))
            {
                logger.Warn(() => $"[{LogCategory}] Payout aborted. Not enough peers (4 required)");
                return;
            }

            // Check gas fee in the beginning once for scheduled payout
            if(extraConfig.EnableGasLimit)
            {
                var latestGasFee = await GetLatestGasFee();
                if(latestGasFee > extraConfig.MaxGasLimit)
                {
                    logger.Warn(() => $"[{LogCategory}] All {balances.Length} payouts deferred until next time. Latest gas fee is above par limit " +
                                      $"({latestGasFee}>{extraConfig.MaxGasLimit})");
                    return;
                }
            }

            var txHashes = new Dictionary<TransactionReceipt, Balance>();
            var logInfo = string.Empty;

            foreach(var balance in balances)
            {
                if(ct.IsCancellationRequested)
                {
                    logger.Info($"[{LogCategory}] Payouts canceled after paying user{logInfo}");
                    break;
                }

                try
                {
                    // On-demand payout is based on gas fee so no need to check again
                    if(extraConfig.EnableGasLimit)
                    {
                        //Check if gas fee is below par range
                        var latestGasFee = await GetLatestGasFee();
                        var lastPaymentDate = balance.PaidDate;
                        var maxGasLimit = lastPaymentDate.HasValue && (clock.UtcNow - lastPaymentDate.Value).TotalHours <= extraConfig.GasLimitToleranceHrs
                            ? extraConfig.GasLimit
                            : extraConfig.MaxGasLimit;
                        if(latestGasFee > maxGasLimit)
                        {
                            logger.Warn(() => $"[{LogCategory}] Payout deferred until next time. Latest gas fee is above par limit " +
                                              $"({latestGasFee}>{maxGasLimit}), lastPmt={lastPaymentDate}, address={balance.Address}");
                            continue;
                        }

                        logger.Info(() => $"[{LogCategory}] Latest gas fee is within par limit ({latestGasFee}<={maxGasLimit}), " +
                                          $"lastPmt={lastPaymentDate}, address={balance.Address}");
                    }

                    logInfo = $", address={balance.Address}";
                    var receipt = await PayoutAsync(balance);
                    if(receipt != null) txHashes.Add(receipt, balance);
                }
                catch(Nethereum.JsonRpc.Client.RpcResponseException ex)
                {
                    if(ex.Message.Contains("Insufficient funds", StringComparison.OrdinalIgnoreCase))
                    {
                        logger.Warn($"[{LogCategory}] {ex.Message}{logInfo}");
                    }
                    else
                    {
                        logger.Error(ex, $"[{LogCategory}] {ex.Message}{logInfo}");
                    }
                    NotifyPayoutFailure(poolConfig.Id, new[] { balance }, ex.Message, null);
                }
                catch(Exception ex)
                {
                    logger.Error(ex, $"[{LogCategory}] {ex.Message}{logInfo}");
                    NotifyPayoutFailure(poolConfig.Id, new[] { balance }, ex.Message, null);
                }
            }

            if(txHashes.Any())
                NotifyPayoutSuccess(poolConfig.Id, txHashes, null);

            logger.Info(() => $"[{LogCategory}] Payouts complete. Successfully processed {txHashes.Count} of {balances.Length} payouts.");
        }

        public async Task<TransactionReceipt> PayoutAsync(Balance balance)
        {
            if(balance.Amount.CompareTo(MaxPayout) > 0)
            {
                logger.Error(() => $"[{LogCategory}] Aborting payout of more than maximum in a single transaction amount: {balance.Amount} wallet {balance.Address}");
                throw new Exception("Aborting payout over maximum amount");
            }

            TransactionReceipt receipt;
            // If web3Connection was created, payout from self managed wallet
            if(web3Connection != null)
            {
                receipt = await PayoutWebAsync(balance);
            }
            else // else payout from daemon managed wallet
            {
                if(!string.IsNullOrEmpty(extraConfig.PrivateKey))
                {
                    logger.Error(() => $"[{LogCategory}] Web3 is configured, but web3Connection is null!");
                    throw new Exception($"Unable to process payouts because web3 is null");
                }

                try
                {
                    var unlockResponse = await daemon.ExecuteCmdSingleAsync<object>(logger, EthCommands.UnlockAccount, new[]
                    {
                        poolConfig.Address,
                        extraConfig.CoinbasePassword,
                        null
                    });

                    if(unlockResponse.Error != null || unlockResponse.Response == null || (bool) unlockResponse.Response == false)
                    {
                        logger.Warn(() => $"[{LogCategory}] Account Unlock failed. Code={unlockResponse.Error.Code}, Data={unlockResponse.Error.Data}, Msg={unlockResponse.Error.Message}");
                    }
                }
                catch
                {
                    throw new Exception("Unable to unlock coinbase account for sending transaction");
                }

                var amount = (BigInteger) Math.Floor(balance.Amount * EthereumConstants.Wei);
                // send transaction
                logger.Info(() => $"[{LogCategory}] Sending {FormatAmount(balance.Amount)} {amount} to {balance.Address}");

                var request = new SendTransactionRequest
                {
                    From = poolConfig.Address,
                    To = balance.Address,
                    Value = amount,
                    Gas = extraConfig.Gas
                };

                // ToDo test difference
                // NL: Value = (BigInteger) Math.Floor(balance.Amount * EthereumConstants.Wei),
                // AX: Value = writeHex(amount),
                var response = await daemon.ExecuteCmdSingleAsync<string>(logger, EthCommands.SendTx, new[] { request });

                if(response.Error != null)
                    throw new Exception($"{EthCommands.SendTx} returned error: {response.Error.Message} code {response.Error.Code}");

                if(string.IsNullOrEmpty(response.Response) || EthereumConstants.ZeroHashPattern.IsMatch(response.Response))
                    throw new Exception($"{EthCommands.SendTx} did not return a valid transaction hash");

                receipt = new TransactionReceipt { Id = response.Response };
            }

            if(receipt != null)
            {
                logger.Info(() => $"[{LogCategory}] Payout transaction id: {receipt.Id}");
                // update db
                await PersistPaymentsAsync(new[] { balance }, receipt.Id);
            }

            // done
            return receipt;
        }

        public decimal GetTransactionDeduction(decimal amount)
        {
            if(extraConfig.GasDeductionPercentage < 0 || extraConfig.GasDeductionPercentage > 100)
            {
                throw new Exception($"Invalid GasDeductionPercentage: {extraConfig.GasDeductionPercentage}");
            }

            return amount * extraConfig.GasDeductionPercentage / 100;
        }

        public bool MinersPayTxFees()
        {
            return extraConfig?.MinersPayTxFees == true;
        }

        public async Task<decimal> GetWalletBalance()
        {
            if(web3Connection == null) return 0;

            try
            {
                var balance = await web3Connection.Eth.GetBalance.SendRequestAsync(poolConfig.Address);
                return Web3.Convert.FromWei(balance.Value);
            }
            catch(Exception ex)
            {
                logger.Error(ex, "Error while fetching wallet balance");
                return 0;
            }
        }

        public void OnDemandPayoutAsync()
        {
            messageBus.Listen<NetworkBlockNotification>().Subscribe(block =>
            {
                logger.Info($"[{LogCategory}] NetworkBlockNotification height={block.BlockHeight}, gasfee={block.BaseFeePerGas}");

                // Handle an invalid gas fee
                if(block.BaseFeePerGas <= 0)
                {
                    logger.Warn($"[{LogCategory}] NetworkBlockNotification invalid gas fee value, gasfee={block.BaseFeePerGas}");
                    return;
                }

                // Check if we are over our hard limit
                if(block.BaseFeePerGas > extraConfig.MaxGasLimit)
                {
                    logger.Info($"[{LogCategory}] Gas exceeds the MaxGasLimit={extraConfig.MaxGasLimit}, skipping payouts");
                    return;
                }

                var minimumPayout = GetMinimumPayout(poolConfig.PaymentProcessing.MinimumPayment, block.BaseFeePerGas, extraConfig.GasDeductionPercentage, extraConfig.Gas);

                // Trigger payouts
                if(ondemandPayTask == null || ondemandPayTask.IsCompleted)
                {
                    logger.Info($"[{LogCategory}] Triggering a new on-demand payouts since gas is below {extraConfig.MaxGasLimit}, gasfee={block.BaseFeePerGas}, minimumPayout={minimumPayout}");
                    ondemandPayTask = PayoutBalancesOverThresholdAsync(minimumPayout);
                }
                else
                {
                    logger.Info($"[{LogCategory}] Existing on-demand payouts is still processing, gasfee={block.BaseFeePerGas}");
                }
            });
        }

        #endregion // IPayoutHandler

        internal static decimal GetMinimumPayout(decimal defaultMinimumPayout, ulong baseFeePerGas, decimal gasDeductionPercentage, ulong transferGas)
        {
            // The default minimum payout is based on the configuration
            var minimumPayout = defaultMinimumPayout;

            // If the GasDeductionPercentage is set, use a ratio instead
            if(gasDeductionPercentage > 0)
            {
                var latestGasFee = baseFeePerGas / EthereumConstants.Wei;
                if(latestGasFee <= 0)
                {
                    throw new Exception($"LatestGasFee is invalid: {latestGasFee}");
                }

                var transactionCost = transferGas * latestGasFee;
                // preSkimPayout = transactionCost / deductionPercentage
                // minimumPayout = preSkimPayout - transactionCost
                // minimumPayout = transactionCost / deductionPercentage - transactionCost

                // alternatively:
                // minimumPayout = (transactionCost / deductionPercentage) * (1 - deductionPercentage)
                // => minimumPayout = transactionCost / deductionPercentage - transactionCost
                minimumPayout = transactionCost / (gasDeductionPercentage / 100) - transactionCost;
            }

            return minimumPayout;
        }

        private void InitializeWeb3(DaemonEndpointConfig daemonConfig)
        {
            if(string.IsNullOrEmpty(extraConfig.PrivateKey)) return;

            var txEndpoint = daemonConfig;
            var protocol = (txEndpoint.Ssl || txEndpoint.Http2) ? "https" : "http";
            var txEndpointUrl = $"{protocol}://{txEndpoint.Host}:{txEndpoint.Port}";

            var account = chainId != 0 ? new Account(extraConfig.PrivateKey, chainId) : new Account(extraConfig.PrivateKey);
            web3Connection = new Web3(account, txEndpointUrl);
        }

        private async Task<DaemonResponses.Block[]> FetchBlocks(Dictionary<long, DaemonResponses.Block> blockCache, params long[] blockHeights)
        {
            var cacheMisses = blockHeights.Where(x => !blockCache.ContainsKey(x)).ToArray();

            if(cacheMisses.Any())
            {
                var blockBatch = cacheMisses.Select(height => new DaemonCmd(EthCommands.GetBlockByNumber,
                    new[]
                    {
                        (object) height.ToStringHexWithPrefix(),
                        true
                    })).ToArray();

                var tmp = await daemon.ExecuteBatchAnyAsync(logger, blockBatch);

                var transformed = tmp
                    .Where(x => x.Error == null && x.Response != null)
                    .Select(x => x.Response?.ToObject<DaemonResponses.Block>())
                    .Where(x => x != null)
                    .ToArray();

                foreach(var block in transformed)
                    blockCache[(long) block.Height.Value] = block;
            }

            return blockHeights.Select(x => blockCache[x]).ToArray();
        }

        private static decimal GetBaseBlockReward(ParityChainType chainType, ulong height)
        {
            switch(chainType)
            {
                case ParityChainType.Mainnet:
                    if(height >= EthereumConstants.ConstantinopleHardForkHeight)
                        return EthereumConstants.ConstantinopleReward;
                    if(height >= EthereumConstants.ByzantiumHardForkHeight)
                        return EthereumConstants.ByzantiumBlockReward;

                    return EthereumConstants.HomesteadBlockReward;

                case ParityChainType.Classic:
                    {
                        var era = Math.Floor(((double) height + 1) / EthereumClassicConstants.BlockPerEra);
                        return (decimal) Math.Pow((double) EthereumClassicConstants.BasePercent, era) * EthereumClassicConstants.BaseRewardInitial;
                    }

                case ParityChainType.Expanse:
                    return EthereumConstants.ExpanseBlockReward;

                case ParityChainType.Ellaism:
                    return EthereumConstants.EllaismBlockReward;

                case ParityChainType.Ropsten:
                    return EthereumConstants.ByzantiumBlockReward;

                case ParityChainType.CallistoTestnet:
                case ParityChainType.Callisto:
                    return CallistoConstants.BaseRewardInitial * (1.0m - CallistoConstants.TreasuryPercent);

                case ParityChainType.Joys:
                    return EthereumConstants.JoysBlockReward;

                default:
                    throw new Exception("Unable to determine block reward: Unsupported chain type");
            }
        }

        private async Task<decimal> GetTxRewardAsync(DaemonResponses.Block blockInfo)
        {
            // fetch all tx receipts in a single RPC batch request
            var batch = blockInfo.Transactions.Select(tx => new DaemonCmd(EthCommands.GetTxReceipt, new[] { tx.Hash }))
                .ToArray();

            var results = await daemon.ExecuteBatchAnyAsync(logger, batch);

            if(results.Any(x => x.Error != null))
                throw new Exception($"Error fetching tx receipts: {string.Join(", ", results.Where(x => x.Error != null).Select(y => y.Error.Message))}");

            // create lookup table
            var gasUsed = results.Select(x => x.Response.ToObject<DaemonResponses.TransactionReceipt>())
                .ToDictionary(x => x.TransactionHash, x => x.GasUsed);

            // accumulate
            var result = blockInfo.Transactions.Sum(x => (ulong) gasUsed[x.Hash] * ((decimal) x.GasPrice / EthereumConstants.Wei));

            return result;
        }

        private static decimal GetUncleReward(ParityChainType chainType, ulong uheight, ulong height)
        {
            var reward = GetBaseBlockReward(chainType, height);

            switch(chainType)
            {
                case ParityChainType.Classic:
                    reward *= EthereumClassicConstants.UnclePercent;
                    break;

                default:
                    // https://ethereum.stackexchange.com/a/27195/18000
                    reward *= uheight + 8 - height;
                    reward /= 8m;
                    break;
            }

            return reward;
        }

        private async Task DetectChainAsync()
        {
            var commands = new[]
            {
                new DaemonCmd(EthCommands.GetNetVersion),
                new DaemonCmd(EthCommands.ParityChain),
                new DaemonCmd(EthCommands.ChainId),
            };

            var results = await daemon.ExecuteBatchAnyAsync(logger, commands);

            if(results.Any(x => x.Error != null))
            {
                if(results[1].Error != null)
                {
                    isParity = false;
                    logger.Info(() => $"Parity is not detected. Switching to Geth.");
                }
                var errors = results.Take(1).Where(x => x.Error != null)
                    .ToArray();

                if(errors.Any())
                    throw new Exception($"Chain detection failed: {string.Join(", ", errors.Select(y => y.Error.Message))}");
            }

            // convert network
            var netVersion = results[0].Response.ToObject<string>();
            var parityChain = isParity ? results[1].Response.ToObject<string>() : (extraPoolConfig?.ChainTypeOverride ?? "Mainnet");
            var chainIdResult = results[2]?.Response?.ToObject<string>();

            logger.Info(() => $"Ethereum chain ID: {chainId}");

            logger.Debug(() => $"Ethereum network: {netVersion}");

            EthereumUtils.DetectNetworkAndChain(netVersion, parityChain, chainIdResult ?? "0", out networkType, out chainType, out chainId);

            if(chainType == ParityChainType.Unknown)
            {
                logger.Warn(() => $"Unable to determine parity chain type: " + parityChain);
            }

            if(networkType == EthereumNetworkType.Unknown)
            {
                logger.Warn(() => $"Unable to determine Ethereum network type: " + netVersion);
            }

            if(chainId == 0)
            {
                logger.Warn(() => $"Unable to determine Ethereum Chain ID: " + chainIdResult);
            }
        }

        private async Task<decimal> CalculateBlockData(PoolConfig poolConfig)
        {
            var blockReward = await GetNetworkBlockReward(poolConfig);
            var stats = await cf.Run(con => statsRepo.GetLastPoolStatsAsync(con, poolConfig.Id));
            var poolStats = new PoolStats();
            BlockchainStats blockChainStats = null;
            if(stats != null)
            {
                poolStats = mapper.Map<PoolStats>(stats);
                blockChainStats = mapper.Map<BlockchainStats>(stats);
            }

            var networkHashRate = blockChainStats.NetworkHashrate;
            double poolHashRate = poolStats.PoolHashrate;

            if(networkHashRate == 0)
            {
                throw new Exception($"Invalid state in CalculateBlockData - NetworkHashRate is 0");
            }

            if(poolHashRate == 0)
            {
                throw new Exception($"Invalid state in CalculateBlockData - PoolHashRate is 0");
            }

            //double avgBlockTime = blockChainStats.NetworkDifficulty / networkHashRate;
            var avgBlockTimeHashrate = await GetNetworkBlockAverageTime(poolConfig);
            var avgBlockTimeHistory = await GetPoolBlockAverageTime(poolConfig);

            if(avgBlockTimeHashrate == 0)
            {
                throw new Exception($"Invalid state in CalculateBlockData - AvgBlockTime is 0");
            }

            var blockFrequencyHashrate = networkHashRate / poolHashRate * (avgBlockTimeHashrate / Sixty);
            var blockFrequencyHistory = avgBlockTimeHistory > 0 ? avgBlockTimeHistory / Sixty : 0;

            double maxBlockFrequency = poolConfig.PaymentProcessing.MaxBlockFrequency;
            if(blockFrequencyHashrate > maxBlockFrequency)
            {
                blockFrequencyHashrate = maxBlockFrequency;
            }

            double payoutInterval = clusterConfig.PaymentProcessing.Interval;

            if(payoutInterval == 0)
            {
                logger.Warn(() => $"Payments are misconfigured. Interval should not be zero. Using the default interal of {DefaultPayoutInterval}");
                payoutInterval = DefaultPayoutInterval;
            }

            double pRatio = 1.0d;

            var now = DateTime.UtcNow;

            var poolState = await TelemetryUtil.TrackDependency(() => cf.Run(con => paymentRepo.GetPoolState(con, poolConfig.Id)),
                DependencyType.Sql, "GetPoolState", "GetLastPayout");

            if(poolState.LastPayout.HasValue && poolState.LastPayout.Value > now.AddDays(-7))
            {
                var sinceLastPayout = now - poolState.LastPayout.Value;
                pRatio = Math.Round(sinceLastPayout.TotalSeconds / payoutInterval, 1);
                payoutInterval = sinceLastPayout.TotalSeconds;
                logger.Info(() => $"Using payoutInterval from database. {payoutInterval}");
            }
            else
            {
                logger.Warn(() => $"payoutInterval from database is invalid or too old: {poolState.LastPayout}. Using interval from config");
            }

            // Try to get the recipient share from the config
            var recipientShare = extraConfig.RecipientShare;
            if(recipientShare <= 0 || recipientShare > 1)
            {
                recipientShare = DefaultRecipientShare;
                logger.Info(() => $"Using the default recipient share of {recipientShare}");
            }
            else
            {
                logger.Info(() => $"Using the configured recipient share of {recipientShare}");
            }

            // Try to get the MaxBlockReward from the config
            var maxBlockReward = extraConfig.MaxBlockReward;
            if(maxBlockReward <= 0 || maxBlockReward > 1)
            {
                maxBlockReward = DefaultMaxBlockReward;
                logger.Info(() => $"Using the default max block reward of {maxBlockReward}");
            }
            else
            {
                logger.Info(() => $"Using the configured max block reward of {maxBlockReward}");
            }

            var recipientBlockReward = (double) (blockReward * recipientShare);
            var blockFrequencyHashratePerPayout = blockFrequencyHashrate / (payoutInterval / Sixty);
            var blockFrequencyHistoryPerPayout = blockFrequencyHistory / (payoutInterval / Sixty);
            var blockData = recipientBlockReward / blockFrequencyHashratePerPayout;
            var blockDataByHistory = blockFrequencyHistoryPerPayout > 0 ? recipientBlockReward / blockFrequencyHistoryPerPayout : 0;
            logger.Info(() => $"HashrateBlockData: {blockData}, NetworkBlockTime: {avgBlockTimeHashrate}, BlockFreqHashrate: {blockFrequencyHashrate}, PayoutInterval: {payoutInterval}, BlockReward: {recipientBlockReward}");
            logger.Info(() => $"HistoryBlockData: {blockDataByHistory}, PoolBlockTime: {avgBlockTimeHistory}, BlockFreqHistory: {blockFrequencyHistory}, BlockFreqPerPayout: {blockFrequencyHistoryPerPayout}");

            if(extraConfig.PoolBlockAvgTimeCalc && blockDataByHistory > 0) blockData = blockDataByHistory;

            // When checking against this threshold, we should take the LastPayout value into account. 
            // For example, if payoutInterval is 10m, but  LastPayout is 30m ago, then we should consider 3x maxBlockReward
            if(blockData > pRatio * maxBlockReward)
            {
                logger.Error(() => $"Rewards calculation data is invalid. BlockData is above max threshold of {pRatio * maxBlockReward}.");
                throw new Exception("Invalid data for calculating mining rewards.  Aborting updateBalances");
            }

            return (decimal) blockData;
        }

        private async Task<decimal> GetNetworkBlockReward(PoolConfig poolConfig)
        {
            if(Cache.TryGetValue(BlockReward, out decimal blockReward))
            {
                return blockReward;
            }

            var esApi = ctx.Resolve<EtherScanEndpoint>();
            var blockResp = await esApi.GetDailyBlockCount(poolConfig.EtherScan.DaysToLookBack);
            if(blockResp == null || blockResp.Length == 0)
            {
                throw new InvalidDataException("GetNetworkBlockReward failed");
            }
            var block = blockResp.First();
            blockReward = block.BlockRewardsEth / block.BlockCount;
            //Add blockReward to cache and set cache data expiration to 24 hours
            logger.Info(() => $"Block Reward from EtherScan: {blockReward}");
            Cache.Set(BlockReward, blockReward, TimeSpan.FromHours(TwentyFourHrs));

            return blockReward;
        }

        private async Task<double> GetNetworkBlockAverageTime(PoolConfig poolConfig)
        {
            if(Cache.TryGetValue(BlockAvgTime, out double blockAvgTime))
            {
                return blockAvgTime;
            }

            var esApi = ctx.Resolve<EtherScanEndpoint>();
            var blockResp = await esApi.GetDailyAverageBlockTime(poolConfig.EtherScan.DaysToLookBack);
            if(blockResp == null || blockResp.Length == 0)
            {
                throw new InvalidDataException("GetNetworkBlockAverageTime failed");
            }
            var block = blockResp.First();
            blockAvgTime = block.BlockTimeSec;
            //Add blockReward to cache and set cache data expiration to 24 hours
            logger.Info(() => $"Block avg time from EtherScan: {blockAvgTime}");
            Cache.Set(BlockAvgTime, blockAvgTime, TimeSpan.FromHours(TwentyFourHrs));

            return blockAvgTime;
        }

        private async Task<double> GetPoolBlockAverageTime(PoolConfig poolConfig)
        {
            if(Cache.TryGetValue(PoolBlockAvgTime, out double blockAvgTime))
            {
                return blockAvgTime;
            }

            var esApi = ctx.Resolve<EtherScanEndpoint>();
            blockAvgTime = await esApi.GetDailyAverageBlockTime(extraConfig.PoolBlockAvgHistory, poolConfig.Address);
            
            //Add blockReward to cache and set cache data expiration to 24 hours
            logger.Info(() => $"Pool block avg time from EtherScan: {blockAvgTime}");
            Cache.Set(PoolBlockAvgTime, blockAvgTime, TimeSpan.FromHours(24));

            return blockAvgTime;
        }

        private async Task<TransactionReceipt> PayoutWebAsync(Balance balance)
        {
            var txCts = new CancellationTokenSource();
            var txHash = string.Empty;
            try
            {
                logger.Info($"[{LogCategory}] Web3Tx start. addr={balance.Address},amt={balance.Amount}");

                var txService = web3Connection.Eth;
                if(txService != null)
                {
                    BigInteger? nonce = null;
                    // Use existing nonce to avoid duplicate transaction
                    Nethereum.RPC.Eth.DTOs.TransactionReceipt txReceipt;
                    if(transactionHashes.TryGetValue(balance.Address, out var prevTxHash))
                    {
                        // Check if existing transaction was succeeded
                        txReceipt = await TelemetryUtil.TrackDependency(() => txService.Transactions.GetTransactionReceipt.SendRequestAsync(prevTxHash),
                            DependencyType.Web3, "GetTransactionReceipt", $"addr={balance.Address},amt={balance.Amount.ToStr()},txhash={prevTxHash}");

                        if(txReceipt != null)
                        {
                            if(txReceipt.HasErrors().GetValueOrDefault())
                            {
                                logger.Error($"[{LogCategory}] Web3Tx failed without a receipt. addr={balance.Address},amt={balance.Amount.ToStr()},status={txReceipt.Status}");
                                return null;
                            }
                            logger.Info($"[{LogCategory}] Web3Tx receipt found for existing tx. addr={balance.Address},amt={balance.Amount.ToStr()},txhash={prevTxHash},gasfee={txReceipt.EffectiveGasPrice}");

                            transactionHashes.TryRemove(balance.Address, out _);
                            return new TransactionReceipt
                            {
                                Id = txReceipt.TransactionHash,
                                Fees = Web3.Convert.FromWei(txReceipt.EffectiveGasPrice),
                                Fees2 = Web3.Convert.FromWei(txReceipt.GasUsed)
                            };
                        }
                        logger.Info($"[{LogCategory}] Web3Tx fetching nonce. addr={balance.Address},amt={balance.Amount},txhash={prevTxHash}");
                        // Get nonce for existing transaction
                        var prevTx = await TelemetryUtil.TrackDependency(() => txService.Transactions.GetTransactionByHash.SendRequestAsync(prevTxHash),
                            DependencyType.Web3, "GetTransactionByHash", $"addr={balance.Address},amt={balance.Amount.ToStr()},txhash={prevTxHash}");
                        nonce = prevTx?.Nonce?.Value;
                        logger.Info($"[{LogCategory}] Web3Tx receipt not found for existing tx. addr={balance.Address},amt={balance.Amount},txhash={prevTxHash},nonce={nonce}");
                    }

                    // Queue transaction
                    txHash = await TelemetryUtil.TrackDependency(() => txService.GetEtherTransferService().TransferEtherAsync(balance.Address, balance.Amount, nonce: nonce),
                        DependencyType.Web3, "TransferEtherAsync", $"addr={balance.Address},amt={balance.Amount.ToStr()},nonce={nonce}");

                    if(string.IsNullOrEmpty(txHash) || EthereumConstants.ZeroHashPattern.IsMatch(txHash))
                    {
                        logger.Error($"[{LogCategory}] Web3Tx failed without a valid transaction hash. addr={balance.Address},amt={balance.Amount.ToStr()}");
                        return null;
                    }
                    logger.Info($"[{LogCategory}] Web3Tx queued. addr={balance.Address},amt={balance.Amount.ToStr()},txhash={txHash}");

                    // Wait for transaction receipt
                    txCts.CancelAfter(TimeSpan.FromMinutes(5)); // Timeout tx after 5min
                    txReceipt = await TelemetryUtil.TrackDependency(() => txService.TransactionManager.TransactionReceiptService.PollForReceiptAsync(txHash, txCts),
                        DependencyType.Web3, "PollForReceiptAsync", $"addr={balance.Address},amt={balance.Amount.ToStr()},txhash={txHash}");
                    if(txReceipt.HasErrors().GetValueOrDefault())
                    {
                        logger.Error($"[{LogCategory}] Web3Tx failed without a receipt. addr={balance.Address},amt={balance.Amount.ToStr()},status={txReceipt.Status},txhash={txHash}");
                        return null;
                    }
                    logger.Info($"[{LogCategory}] Web3Tx receipt received. addr={balance.Address},amt={balance.Amount},txhash={txHash},gasfee={txReceipt.EffectiveGasPrice}");
                    // Release address from pending list if successfully paid out
                    if(transactionHashes.ContainsKey(balance.Address)) transactionHashes.TryRemove(balance.Address, out _);

                    return new TransactionReceipt
                    {
                        Id = txReceipt.TransactionHash,
                        Fees = Web3.Convert.FromWei(txReceipt.EffectiveGasPrice),
                        Fees2 = Web3.Convert.FromWei(txReceipt.GasUsed)
                    };
                }

                logger.Warn($"[{LogCategory}] Web3Tx GetEtherTransferService is null. addr={balance.Address}, amt={balance.Amount}");
            }
            catch(OperationCanceledException)
            {
                if(!string.IsNullOrEmpty(txHash))
                {
                    transactionHashes.AddOrUpdate(balance.Address, txHash, (_, _) => txHash);
                }
                logger.Warn($"[{LogCategory}] Web3Tx transaction timed out. addr={balance.Address},amt={balance.Amount.ToStr()},txhash={txHash},pendingTxs={transactionHashes.Count}");
            }
            catch(Nethereum.JsonRpc.Client.RpcResponseException ex)
            {
                logger.Error(ex, $"[{LogCategory}] Web3Tx failed. {ex.Message}");
            }
            finally
            {
                txCts.Dispose();
            }

            return null;
        }

        private async Task PayoutBalancesOverThresholdAsync(decimal minimumPayout)
        {
            logger.Info(() => $"[{LogCategory}] Processing payout for pool [{poolConfig.Id}]");

            try
            {
                // Get the balances over the dynamically calculated threshold
                var poolBalancesOverMinimum = await TelemetryUtil.TrackDependency(
                    () => cf.Run(con => balanceRepo.GetPoolBalancesOverThresholdAsync(con, poolConfig.Id, minimumPayout, extraConfig.PayoutBatchSize)),
                    DependencyType.Sql,
                    "GetPoolBalancesOverThresholdAsync",
                    $"minimumPayout={minimumPayout}");

                // Payout the balances above the threshold
                if(poolBalancesOverMinimum.Length > 0)
                {
                    await TelemetryUtil.TrackDependency(
                        () => PayoutBatchAsync(poolBalancesOverMinimum, minimumPayout),
                        DependencyType.Sql,
                        "PayoutBalancesOverThresholdAsync",
                        $"miners:{poolBalancesOverMinimum.Length},minimumPayout={minimumPayout}");
                }
                else
                {
                    logger.Info(() => $"[{LogCategory}] No balances over calculated minimum payout {minimumPayout.ToStr()} for pool {poolConfig.Id}");
                }
            }
            catch(Exception ex)
            {
                logger.Error(ex, $"[{LogCategory}] Error while processing payout balances over threshold");
            }
        }

        private async Task PayoutBatchAsync(Balance[] balances, decimal minimumPayout)
        {
            logger.Info(() => $"[{LogCategory}] Beginning payout to top {extraConfig.PayoutBatchSize} miners.");

            // ensure we have peers
            var infoResponse = await daemon.ExecuteCmdSingleAsync<string>(logger, EthCommands.GetPeerCount);
            if(networkType == EthereumNetworkType.Main && (infoResponse.Error != null || string.IsNullOrEmpty(infoResponse.Response) || infoResponse.Response.IntegralFromHex<int>() < EthereumConstants.MinPayoutPeerCount))
            {
                logger.Warn(() => $"[{LogCategory}] Payout aborted. Not enough peers (4 required)");
                return;
            }

            if(!string.IsNullOrEmpty(extraConfig.PrivateKey) && web3Connection == null) InitializeWeb3(daemonEndpointConfig);

            var txHashes = new Dictionary<TransactionReceipt, Balance>();
            var payTasks = new List<Task>(balances.Length);

            foreach(var balance in balances)
            {
                var balanceOverThreshold = balance.Amount - minimumPayout;
                if(balanceOverThreshold > 0)
                {
                    decimal gasFeesOvercharged = (balanceOverThreshold / (1 - (extraConfig.GasDeductionPercentage / 100))) - balanceOverThreshold;
                    logger.Info(() => $"[{LogCategory}] miner:{balance.Address}, amount:{balance.Amount}, minimumPayout:{minimumPayout}, gasDeduction:{extraConfig.GasDeductionPercentage}, overcharge:{gasFeesOvercharged}");
                }

                payTasks.Add(Task.Run(async () =>
                {
                    var logInfo = $",addr={balance.Address},amt={balance.Amount.ToStr()}";
                    try
                    {
                        var receipt = await PayoutAsync(balance);
                        if(receipt != null)
                        {
                            lock(txHashes)
                            {
                                txHashes.Add(receipt, balance);
                            }
                        }
                    }
                    catch(Nethereum.JsonRpc.Client.RpcResponseException ex)
                    {
                        if(ex.Message.Contains("Insufficient funds", StringComparison.OrdinalIgnoreCase))
                        {
                            logger.Warn($"[{LogCategory}] {ex.Message}{logInfo}");
                        }
                        else
                        {
                            logger.Error(ex, $"[{LogCategory}] {ex.Message}{logInfo}");
                        }

                        NotifyPayoutFailure(poolConfig.Id, new[] { balance }, ex.Message, null);
                    }
                    catch(Exception ex)
                    {
                        logger.Error(ex, $"[{LogCategory}] {ex.Message}{logInfo}");
                        NotifyPayoutFailure(poolConfig.Id, new[] { balance }, ex.Message, null);
                    }
                }));
            }
            // Wait for all payment to finish
            await Task.WhenAll(payTasks);

            if(txHashes.Any()) NotifyPayoutSuccess(poolConfig.Id, txHashes, null);
            // Reset web3 when transactions are failing
            if(txHashes.Count < balances.Length) web3Connection = null;

            logger.Info(() => $"[{LogCategory}] Payouts complete.  Successfully processed top {txHashes.Count} of {balances.Length} payouts.");
        }

        private async Task<ulong?> GetLatestGasFee()
        {
            var latestBlockResp = await TelemetryUtil.TrackDependency(() => daemon.ExecuteCmdAnyAsync<DaemonResponses.Block>(
                logger, EthCommands.GetBlockByNumber, new[] { (object) "latest", true }), DependencyType.Daemon, "GetLatestGasFee", "GetLatestGasFee");
            logger.Info(() => $"Fetched latest gas fee from network: {latestBlockResp?.Response.BaseFeePerGas}");

            return latestBlockResp?.Response.BaseFeePerGas;
        }
    }
}
