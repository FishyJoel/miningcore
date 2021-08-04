using System.Numerics;
using System.Threading.Tasks;
using Miningcore.Blockchain.Ethereum;
using Xunit;

namespace Miningcore.Tests.Blockchain.Ethereum
{
    public class EthereumUtilsTests : TestBase
    {

        [Fact]
        public async Task DetectNetworkAndChain_Hex_WithPrefix()
        {
            EthereumUtils.DetectNetworkAndChain("1", "ethereum classic", "0x3",
                out EthereumNetworkType ethereumNetworkType, out ParityChainType parityChainType, out BigInteger chainId);

            Assert.Equal(EthereumNetworkType.Main, ethereumNetworkType);
            Assert.Equal(ParityChainType.Classic, parityChainType);
            Assert.Equal(3, chainId);
        }

        [Fact]
        public async Task DetectNetworkAndChain_Hex_Prefix_UpperCase()
        {
            EthereumUtils.DetectNetworkAndChain("1", "ethereum classic", "0X03",
                out EthereumNetworkType ethereumNetworkType, out ParityChainType parityChainType, out BigInteger chainId);

            Assert.Equal(EthereumNetworkType.Main, ethereumNetworkType);
            Assert.Equal(ParityChainType.Classic, parityChainType);
            Assert.Equal(3, chainId);
        }

        [Fact]
        public async Task DetectNetworkAndChain_Hex_WithoutPrefix()
        {
            EthereumUtils.DetectNetworkAndChain("1", "ethereum classic", "03",
                out EthereumNetworkType ethereumNetworkType, out ParityChainType parityChainType, out BigInteger chainId);

            Assert.Equal(EthereumNetworkType.Main, ethereumNetworkType);
            Assert.Equal(ParityChainType.Classic, parityChainType);
            Assert.Equal(3, chainId);
        }

        [Fact]
        public async Task DetectNetworkAndChain_Number()
        {
            EthereumUtils.DetectNetworkAndChain("1", "ethereum classic", "3",
                out EthereumNetworkType ethereumNetworkType, out ParityChainType parityChainType, out BigInteger chainId);

            Assert.Equal(EthereumNetworkType.Main, ethereumNetworkType);
            Assert.Equal(ParityChainType.Classic, parityChainType);
            Assert.Equal(3, chainId);
        }
    }
}
