using System;
using Miningcore.Extensions;
using Miningcore.Native;
using Xunit;
using static Miningcore.Native.LibCryptonight;

namespace Miningcore.Tests.Crypto
{
    public class CrytonoteTests : TestBase
    {
        [Fact]
        public void Crytonight()
        {
            var blobConverted = "0106a2aaafd505583cf50bcc743d04d831d2b119dc94ad88679e359076ee3f18d258ee138b3b42580100a4b1e2f4baf6ab7109071ab59bc52dba740d1de99fa0ae0c4afd6ea9f40c5d87ec01".HexToByteArray();
            var buf = new byte[32];

            LibCryptonight.Cryptonight(blobConverted,"" ,buf, CryptonightVariant.VARIANT_0, 0);
            var result = buf.ToHexString();
            Assert.Equal("a845ffbdf83ae9a8ffa504a1011efbd5ed2294bb9da591d3b583740568402c00", result);

            Array.Clear(buf, 0, buf.Length);

            LibCryptonight.Cryptonight(blobConverted,"" , buf, CryptonightVariant.VARIANT_0, 0);
            result = buf.ToHexString();
            Assert.Equal("a845ffbdf83ae9a8ffa504a1011efbd5ed2294bb9da591d3b583740568402c00", result);
        }

        [Fact]
        public void Crytonight_Variant_1()
        {
            var blobConverted = "0106a2aaafd505583cf50bcc743d04d831d2b119dc94ad88679e359076ee3f18d258ee138b3b42580100a4b1e2f4baf6ab7109071ab59bc52dba740d1de99fa0ae0c4afd6ea9f40c5d87ec01".HexToByteArray();
            var buf = new byte[32];

            LibCryptonight.Cryptonight(blobConverted,"" , buf, CryptonightVariant.VARIANT_1, 0);
            var result = buf.ToHexString();
            Assert.Equal("c41ec6434df8b2307ff3105ae15206f3fbdf5a99b35879c0a27b8b85a8e2704f", result);

            Array.Clear(buf, 0, buf.Length);

            LibCryptonight.Cryptonight(blobConverted,"" , buf, CryptonightVariant.VARIANT_1, 0);
            result = buf.ToHexString();
            Assert.Equal("c41ec6434df8b2307ff3105ae15206f3fbdf5a99b35879c0a27b8b85a8e2704f", result);
        }

        [Fact]
        public void Crytonight_Variant_4()
        {
            var blobConverted = "0106a2aaafd505583cf50bcc743d04d831d2b119dc94ad88679e359076ee3f18d258ee138b3b42580100a4b1e2f4baf6ab7109071ab59bc52dba740d1de99fa0ae0c4afd6ea9f40c5d87ec01".HexToByteArray();
            var buf = new byte[32];

            LibCryptonight.Cryptonight(blobConverted,"" , buf, CryptonightVariant.VARIANT_4, 0);
            var result = buf.ToHexString();
            Assert.Equal("3e69817268c70010f793d53ba1a9f12af21753c723c7d7990a8eefccc6d163ba", result);

            Array.Clear(buf, 0, buf.Length);

            LibCryptonight.Cryptonight(blobConverted,"" , buf, CryptonightVariant.VARIANT_4, 0);
            result = buf.ToHexString();
            Assert.Equal("3e69817268c70010f793d53ba1a9f12af21753c723c7d7990a8eefccc6d163ba", result);
        }

        [Fact]
        public void Crytonote_Hash_Fast()
        {
            var blobConverted = "0106a2aaafd505583cf50bcc743d04d831d2b119dc94ad88679e359076ee3f18d258ee138b3b42580100a4b1e2f4baf6ab7109071ab59bc52dba740d1de99fa0ae0c4afd6ea9f40c5d87ec01".HexToByteArray();
            var buf = new byte[32];

            LibCryptonote.CryptonightHashFast(blobConverted, buf);
            var result = buf.ToHexString();
            Assert.Equal("ddc0e3a33b605ce39fa2d16a98d7634e33399ab1e4b56b3bdd3414b655fe9a98", result);
        }

        [Fact]
        public void Crytonight_Light()
        {
            var blobConverted = "0106f1adafd505583cf50bcc743d04d831d2b119dc94ad88679e359076ee3f18d258ee138b3b42597710c48c6d885e2622f40f82ecd9b9fd538f28df9b0557e07cd3237a31c76569ada98001".HexToByteArray();
            var buf = new byte[32];

            LibCryptonight.CryptonightLight(blobConverted,"" , buf, CryptonightVariant.VARIANT_0, 0);
            var result = buf.ToHexString();
            Assert.Equal("0769caee428a232cffb76fa200f174ff962734f24e7b3bf8d1b0d4e8ba6ceebf", result);

            Array.Clear(buf, 0, buf.Length);

            LibCryptonight.CryptonightLight(blobConverted,"" , buf, CryptonightVariant.VARIANT_0, 0);
            result = buf.ToHexString();
            Assert.Equal("0769caee428a232cffb76fa200f174ff962734f24e7b3bf8d1b0d4e8ba6ceebf", result);
        }

        [Fact]
        public void Crytonight_Light_Variant_1()
        {
            var blobConverted = "0106f1adafd505583cf50bcc743d04d831d2b119dc94ad88679e359076ee3f18d258ee138b3b42597710c48c6d885e2622f40f82ecd9b9fd538f28df9b0557e07cd3237a31c76569ada98001".HexToByteArray();
            var buf = new byte[32];

            LibCryptonight.CryptonightLight(blobConverted,"" , buf, CryptonightVariant.VARIANT_0, 0);
            var result = buf.ToHexString();
            Assert.Equal("0769caee428a232cffb76fa200f174ff962734f24e7b3bf8d1b0d4e8ba6ceebf", result);

            Array.Clear(buf, 0, buf.Length);

            LibCryptonight.CryptonightLight(blobConverted,"" , buf, CryptonightVariant.VARIANT_0, 0);
            result = buf.ToHexString();
            Assert.Equal("0769caee428a232cffb76fa200f174ff962734f24e7b3bf8d1b0d4e8ba6ceebf", result);
        }

        [Fact]
        public void Crytonight_Heavy()
        {
            var blobConverted = "0106f1adafd505583cf50bcc743d04d831d2b119dc94ad88679e359076ee3f18d258ee138b3b42597710c48c6d885e2622f40f82ecd9b9fd538f28df9b0557e07cd3237a31c76569ada98001".HexToByteArray();
            var buf = new byte[32];

            LibCryptonight.CryptonightHeavy(blobConverted,"" , buf, CryptonightVariant.VARIANT_0, 0);
            var result = buf.ToHexString();
            Assert.Equal("93b6815d8f19abe0ff8ba8d8cf951cd264aa123e450bd52dc806fac298f83d9f", result);

            Array.Clear(buf, 0, buf.Length);

            LibCryptonight.CryptonightHeavy(blobConverted,"" , buf, CryptonightVariant.VARIANT_0, 0);
            result = buf.ToHexString();
            Assert.Equal("93b6815d8f19abe0ff8ba8d8cf951cd264aa123e450bd52dc806fac298f83d9f", result);
        }

        [Fact]
        public void Crytonight_Heavy_Variant_1()
        {
            var blobConverted = "0106f1adafd505583cf50bcc743d04d831d2b119dc94ad88679e359076ee3f18d258ee138b3b42597710c48c6d885e2622f40f82ecd9b9fd538f28df9b0557e07cd3237a31c76569ada98001".HexToByteArray();
            var buf = new byte[32];

            LibCryptonight.CryptonightHeavy(blobConverted,"" , buf, CryptonightVariant.VARIANT_1, 0);
            var result = buf.ToHexString();
            Assert.Equal("342418ec4bf806aafb102b34d64fc33ab91d89ad40786b92d1b54ceeb4d50822", result);

            Array.Clear(buf, 0, buf.Length);

            LibCryptonight.CryptonightHeavy(blobConverted,"" , buf, CryptonightVariant.VARIANT_1, 0);
            result = buf.ToHexString();
            Assert.Equal("342418ec4bf806aafb102b34d64fc33ab91d89ad40786b92d1b54ceeb4d50822", result);
        }

        [Fact]
        public void Crytonote_ConvertBlob()
        {
            var blob = "0106e5b3afd505583cf50bcc743d04d831d2b119dc94ad88679e359076ee3f18d258ee138b3b421c0300a401d90101ff9d0106d6d6a88702023c62e43372a58cb588147e20be53a27083f5c522f33c722b082ab7518c48cda280b4c4c32102609ec96e2499ee267d70efefc49f26e330526d3ef455314b7b5ba268a6045f8c80c0fc82aa0202fe5cc0fa56c4277d1a47827edce4725571529d57f33c73ada481ef84c323f30a8090cad2c60e02d88bf5e72a611c8b8464ce29e3b1adbfe1ae163886d9150fe511171cada98fcb80e08d84ddcb0102441915aaf9fbaf70ff454c701a6ae2bd59bb94dc0b888bf7e5d06274ee9238ca80c0caf384a302024078526e2132def44bde2806242652f5944e632f7d94290dd6ee5dda1929f5ee2b016e29f25f07ec2a8df59f0e118a6c9a4b769b745dc0c729071f6e0399d2585745020800000000012e7f76000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000".HexToByteArray();
            var buf = new byte[32];

            var result = LibCryptonote.ConvertBlob(blob, 330).ToHexString();
            Assert.Equal("0106e5b3afd505583cf50bcc743d04d831d2b119dc94ad88679e359076ee3f18d258ee138b3b421c0300a4487286e262e95b8d2163a0c8b73527e8c9425adbdc4e532cf0ef4241f9ffbe9e01", result);
        }

        [Fact]
        public void Crytonote_DecodeAddress()
        {
            var address = "48nhyWcSey31ngSEhV8j8NPm6B8PistCQJBjjDjmTvRSTWYg6iocAw131vE2JPh3ps33vgQDKLrUx3fcErusYWcMJBxpm1d";
            var result = LibCryptonote.DecodeAddress(address);

            Assert.Equal(18ul, result);
        }

        [Fact]
        public void Cryptonote_DecodeAddress_Should_Throw_On_Null_Or_Empty_Argument()
        {
            Assert.Throws<ArgumentException>(() => LibCryptonote.DecodeAddress(null));
            Assert.Throws<ArgumentException>(() => LibCryptonote.DecodeAddress(""));
        }

        [Fact]
        public void Crytonote_DecodeIntegratedAddress()
        {
            var address = "4BrL51JCc9NGQ71kWhnYoDRffsDZy7m1HUU7MRU4nUMXAHNFBEJhkTZV9HdaL4gfuNBxLPc3BeMkLGaPbF5vWtANQsGwTGg55Kq4p3ENE7";
            var result = LibCryptonote.DecodeIntegratedAddress(address);

            Assert.Equal(19ul, result);
        }
    }
}
