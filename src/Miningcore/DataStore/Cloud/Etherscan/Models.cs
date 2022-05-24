using System;
using Newtonsoft.Json;

namespace Miningcore.DataStore.Cloud.EtherScan
{
    public class EtherScanResponse<T>
    {
        public int Status { get; set; }
        public string Message { get; set; }
        public T Result { get; set; }

    }

    public class DailyBlkCount
    {
        public DateTime UtcDate { get; set; }
        public string UnixTimeStamp { get; set; }
        public long BlockCount { get; set; }
        [JsonProperty("blockRewards_Eth")]
        public decimal BlockRewardsEth { get; set; }
    }

    public class DailyAverageBlockTime
    {
        public DateTime UtcDate { get; set; }
        public string UnixTimeStamp { get; set; }
        [JsonProperty("blockTime_sec")]
        public double BlockTimeSec { get; set; }
    }

    public class MinedBlocks
    {
        public double BlockNumber { get; set; }
        public double TimeStamp { get; set; }
        public decimal BlockReward { get; set; }
    }
}
