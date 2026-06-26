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

using System.Linq;
using System.Reflection;
using System.Threading;
using Alpaca.Markets;
using Moq;
using NUnit.Framework;
using QuantConnect.Tests;

namespace QuantConnect.Brokerages.Alpaca.Tests
{
    /// <summary>
    /// Credential-free unit tests for <see cref="AlpacaBrokerage.GetCashBalance"/> and
    /// <see cref="AlpacaBrokerage.GetAccountHoldings"/> using a mocked Alpaca trading client.
    /// </summary>
    [TestFixture]
    public class AlpacaBrokerageCashAndHoldingsTests
    {
        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            // Ensures the data folder (symbol properties / market hours databases) is available.
            TestGlobals.Initialize();
        }

        [Test]
        public void GetCashBalanceIncludesCryptoCoinBalances()
        {
            var brokerage = CreateBrokerage(CreateClient(
                usdCash: 98506.5m,
                CreateCryptoPosition("BTC/USD", 0.5m, 60000m),
                CreateCryptoPosition("USDC/USD", 1000m, 1m)));

            var balances = brokerage.GetCashBalance();

            // USD fiat plus each crypto coin reported as its base currency.
            Assert.That(balances.Single(b => b.Currency == "USD").Amount, Is.EqualTo(98506.5m));
            Assert.That(balances.Single(b => b.Currency == "BTC").Amount, Is.EqualTo(0.5m));
            Assert.That(balances.Single(b => b.Currency == "USDC").Amount, Is.EqualTo(1000m));
        }

        [Test]
        public void GetCashBalanceWithNoCryptoReturnsOnlyFiat()
        {
            var brokerage = CreateBrokerage(CreateClient(usdCash: 12345m));

            var balances = brokerage.GetCashBalance();

            Assert.That(balances.Count, Is.EqualTo(1));
            Assert.That(balances[0].Currency, Is.EqualTo("USD"));
            Assert.That(balances[0].Amount, Is.EqualTo(12345m));
        }

        [Test]
        public void GetAccountHoldingsReturnsCryptoWithQuoteCurrencySymbol()
        {
            var brokerage = CreateBrokerage(CreateClient(
                usdCash: 0m,
                CreateCryptoPosition("BTC/USD", 0.5m, 60000m),
                CreateCryptoPosition("USDC/USD", 1000m, 1m)));

            var holdings = brokerage.GetAccountHoldings();

            var btc = holdings.Single(h => h.Symbol.Value == "BTCUSD");
            Assert.That(btc.Quantity, Is.EqualTo(0.5m));
            Assert.That(btc.AveragePrice, Is.EqualTo(60000m));
            Assert.That(btc.CurrencySymbol, Is.EqualTo("$")); // USD quote currency

            // USDC/USD must resolve as a holding (not skipped).
            var usdc = holdings.Single(h => h.Symbol.Value == "USDCUSD");
            Assert.That(usdc.Quantity, Is.EqualTo(1000m));
        }

        private static AlpacaBrokerage CreateBrokerage(IAlpacaTradingClient client)
        {
            // The symbol mapper registers the crypto symbol properties so the pairs resolve.
            var mapper = new AlpacaBrokerageSymbolMapper(client);
            var brokerage = new AlpacaBrokerage();
            SetPrivateField(brokerage, "_tradingClient", client);
            SetPrivateField(brokerage, "_symbolMapper", mapper);
            return brokerage;
        }

        private static void SetPrivateField(object target, string name, object value)
        {
            var field = typeof(AlpacaBrokerage).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, $"Private field '{name}' not found on AlpacaBrokerage");
            field.SetValue(target, value);
        }

        private static IAlpacaTradingClient CreateClient(decimal usdCash, params IPosition[] positions)
        {
            var account = new Mock<IAccount>();
            account.SetupGet(a => a.TradableCash).Returns(usdCash);
            account.SetupGet(a => a.Currency).Returns("USD");

            var assets = positions
                .Where(p => p.AssetClass == AssetClass.Crypto)
                .Select(p => CreateCryptoAsset(p.Symbol))
                .ToArray();

            var client = new Mock<IAlpacaTradingClient>();
            client.Setup(c => c.GetAccountAsync(It.IsAny<CancellationToken>())).ReturnsAsync(account.Object);
            client.Setup(c => c.ListPositionsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(positions);
            client.Setup(c => c.ListAssetsAsync(It.IsAny<AssetsRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(assets);
            return client.Object;
        }

        private static IAsset CreateCryptoAsset(string brokerageSymbol)
        {
            var asset = new Mock<IAsset>();
            asset.SetupGet(a => a.Symbol).Returns(brokerageSymbol);
            asset.SetupGet(a => a.Name).Returns(brokerageSymbol);
            asset.SetupGet(a => a.Class).Returns(AssetClass.Crypto);
            asset.SetupGet(a => a.PriceIncrement).Returns(0.01m);
            asset.SetupGet(a => a.MinTradeIncrement).Returns(0.00000001m);
            asset.SetupGet(a => a.MinOrderSize).Returns(0.0001m);
            return asset.Object;
        }

        private static IPosition CreateCryptoPosition(string brokerageSymbol, decimal quantity, decimal price)
        {
            var position = new Mock<IPosition>();
            position.SetupGet(p => p.Symbol).Returns(brokerageSymbol);
            position.SetupGet(p => p.AssetClass).Returns(AssetClass.Crypto);
            position.SetupGet(p => p.Quantity).Returns(quantity);
            position.SetupGet(p => p.AverageEntryPrice).Returns(price);
            position.SetupGet(p => p.MarketValue).Returns(quantity * price);
            position.SetupGet(p => p.AssetCurrentPrice).Returns(price);
            position.SetupGet(p => p.UnrealizedProfitLoss).Returns(0m);
            position.SetupGet(p => p.UnrealizedProfitLossPercent).Returns(0m);
            return position.Object;
        }
    }
}
