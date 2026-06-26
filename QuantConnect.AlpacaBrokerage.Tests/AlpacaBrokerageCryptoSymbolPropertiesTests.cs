/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
*/

using System.Threading;
using Alpaca.Markets;
using Moq;
using NUnit.Framework;
using QuantConnect.Securities;

namespace QuantConnect.Brokerages.Alpaca.Tests
{
    /// <summary>
    /// Credential-free unit tests for the crypto symbol-properties registration performed by
    /// <see cref="AlpacaBrokerageSymbolMapper"/> using a mocked Alpaca trading client.
    /// </summary>
    [TestFixture]
    public class AlpacaBrokerageCryptoSymbolPropertiesTests
    {
        private static AlpacaBrokerageSymbolMapper CreateMapper(params IAsset[] assets)
        {
            var client = new Mock<IAlpacaTradingClient>();
            client
                .Setup(c => c.ListAssetsAsync(It.IsAny<AssetsRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(assets);
            return new AlpacaBrokerageSymbolMapper(client.Object);
        }

        private static IAsset CreateCryptoAsset(string brokerageSymbol, string name, decimal priceIncrement, decimal minTradeIncrement, decimal minOrderSize)
        {
            var asset = new Mock<IAsset>();
            asset.SetupGet(a => a.Symbol).Returns(brokerageSymbol);
            asset.SetupGet(a => a.Name).Returns(name);
            asset.SetupGet(a => a.Class).Returns(AssetClass.Crypto);
            asset.SetupGet(a => a.PriceIncrement).Returns(priceIncrement);
            asset.SetupGet(a => a.MinTradeIncrement).Returns(minTradeIncrement);
            asset.SetupGet(a => a.MinOrderSize).Returns(minOrderSize);
            return asset.Object;
        }

        [Test]
        public void RegistersSymbolPropertiesForCryptoPairMissingFromDatabase()
        {
            // USDC/USD is tradable on Alpaca but is not a bundled pair in the crypto (Coinbase) market.
            CreateMapper(CreateCryptoAsset("USDC/USD", "USD Coin", 0.0001m, 0.000001m, 1m));

            var symbol = Symbol.Create("USDCUSD", SecurityType.Crypto, Market.Coinbase);
            var properties = SymbolPropertiesDatabase.FromDataFolder()
                .GetSymbolProperties(Market.Coinbase, symbol, SecurityType.Crypto, Currencies.USD);

            // MarketTicker / values come straight from the Alpaca asset, proving the entry was registered
            // (a missing entry would return SymbolProperties defaults with an empty market ticker).
            Assert.That(properties.MarketTicker, Is.EqualTo("USDC/USD"));
            Assert.That(properties.QuoteCurrency, Is.EqualTo(Currencies.USD));
            Assert.That(properties.Description, Is.EqualTo("USD Coin"));
            Assert.That(properties.MinimumPriceVariation, Is.EqualTo(0.0001m));
            Assert.That(properties.LotSize, Is.EqualTo(0.000001m));
            Assert.That(properties.MinimumOrderSize, Is.EqualTo(1m));
        }

        [Test]
        public void MapsRegisteredCryptoSymbolBothDirections()
        {
            var mapper = CreateMapper(
                CreateCryptoAsset("BTC/USD", "Bitcoin", 0.01m, 0.00000001m, 0.0001m),
                CreateCryptoAsset("USDC/USD", "USD Coin", 0.0001m, 0.000001m, 1m));

            var btc = mapper.GetLeanSymbol(AssetClass.Crypto, "BTC/USD");
            Assert.That(btc.Value, Is.EqualTo("BTCUSD"));
            Assert.That(btc.SecurityType, Is.EqualTo(SecurityType.Crypto));
            Assert.That(mapper.GetBrokerageSymbol(btc), Is.EqualTo("BTC/USD"));

            // The previously unresolvable pair now round-trips.
            var usdc = Symbol.Create("USDCUSD", SecurityType.Crypto, Market.Coinbase);
            Assert.That(mapper.GetBrokerageSymbol(usdc), Is.EqualTo("USDC/USD"));
        }
    }
}
