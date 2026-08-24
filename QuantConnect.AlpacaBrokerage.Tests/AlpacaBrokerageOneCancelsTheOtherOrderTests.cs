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

using System;
using Moq;
using NUnit.Framework;
using QuantConnect.Tests;
using QuantConnect.Orders;
using AlpacaMarket = Alpaca.Markets;
using System.Collections.Generic;

namespace QuantConnect.Brokerages.Alpaca.Tests
{
    /// <summary>
    /// Pure unit tests for the one-cancels-the-other (OCO) group validation and request construction. These do
    /// not construct an <see cref="AlpacaBrokerage"/> instance and do not require live/paper trading credentials.
    /// </summary>
    [TestFixture]
    public class AlpacaBrokerageOneCancelsTheOtherOrderTests
    {
        private static readonly DateTime OrderTime = new(2026, 1, 1);

        [TestCase(-100, "sell")]
        [TestCase(100, "buy")]
        public void CreateAlpacaOneCancelsTheOtherOrderBuildsExpectedRequest(decimal quantitySign, string expectedSide)
        {
            var tradingClientMock = new Mock<AlpacaMarket.IAlpacaTradingClient>();
            tradingClientMock
                .Setup(m => m.ListAssetsAsync(It.IsAny<AlpacaMarket.AssetsRequest>(), It.IsAny<System.Threading.CancellationToken>()))
                .ReturnsAsync(new List<AlpacaMarket.IAsset>());
            var symbolMapper = new AlpacaBrokerageSymbolMapper(tradingClientMock.Object);
            var limitLeg = new LimitOrder(Symbols.AAPL, quantitySign, 220m, OrderTime);
            var stopLeg = new StopMarketOrder(Symbols.AAPL, quantitySign, 190m, OrderTime);

            var ocoOrder = limitLeg.CreateAlpacaOneCancelsTheOtherOrder(stopLeg, symbolMapper);

            Assert.AreEqual(AlpacaMarket.OrderClass.OneCancelsOther, ocoOrder.OrderClass);
            Assert.AreEqual(220m, ocoOrder.TakeProfit.LimitPrice);
            Assert.AreEqual(190m, ocoOrder.StopLoss.StopPrice);
            Assert.IsNull(ocoOrder.StopLoss.LimitPrice);
            Assert.AreEqual(expectedSide, ocoOrder.Side.ToString().ToLowerInvariant());
            Assert.AreEqual("AAPL", ocoOrder.Symbol);
        }
    }
}
