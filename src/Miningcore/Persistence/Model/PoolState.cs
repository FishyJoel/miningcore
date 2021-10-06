using System;

namespace Miningcore.Persistence.Model
{
    public class PoolState
    {
        public string PoolId { get; set; }
        public double HashValue { get; set; }
        public DateTime LastPayout { get; set; }
    }
}