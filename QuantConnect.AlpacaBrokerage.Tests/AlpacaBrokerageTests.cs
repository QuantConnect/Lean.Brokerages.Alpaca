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
using System.Linq;
using NUnit.Framework;
using QuantConnect.Tests;
using QuantConnect.Orders;
using QuantConnect.Logging;
using QuantConnect.Interfaces;
using QuantConnect.Securities;
using System.Collections.Generic;
using QuantConnect.Configuration;
using QuantConnect.Tests.Brokerages;
using QuantConnect.Lean.Engine.DataFeeds;

namespace QuantConnect.Brokerages.Alpaca.Tests
{
    [TestFixture]
    public partial class AlpacaBrokerageTests : BrokerageTests
    {
        protected override Symbol Symbol { get; } = Symbols.AAPL;
        protected override SecurityType SecurityType { get; }

        protected override IBrokerage CreateBrokerage(IOrderProvider orderProvider, ISecurityProvider securityProvider)
        {
            var apiKey = Config.Get("alpaca-api-key-id");
            var apiSecret = Config.Get("alpaca-api-secret-key");
            var isPaperTrading = Config.GetBool("alpaca-use-paper-trading");

            return new AlpacaBrokerage(apiKey, apiSecret, isPaperTrading, orderProvider, new AggregationManager());
        }
        protected override bool IsAsync() => false;
        protected override decimal GetAskPrice(Symbol symbol)
        {
            return (Brokerage as AlpacaBrokerage).GetLatestQuote(symbol).AskPrice;
        }


        /// <summary>
        /// Provides the data required to test each order type in various cases
        /// </summary>
        private static IEnumerable<TestCaseData> EquityOrderParameters
        {
            get
            {
                var EPU = Symbol.Create("EPU", SecurityType.Equity, Market.USA);
                yield return new TestCaseData(new MarketOrderTestParameters(EPU));
                yield return new TestCaseData(new LimitOrderTestParameters(EPU, 40m, 30m));
                yield return new TestCaseData(new StopMarketOrderTestParameters(EPU, 40m, 30m));
                yield return new TestCaseData(new StopLimitOrderTestParameters(EPU, 40m, 30m));
            }
        }

        /// <summary>
        /// Provides the data required to test each order type in various cases
        /// </summary>
        private static IEnumerable<TestCaseData> CryptoOrderParameters
        {
            get
            {
                var ETHUSD = Symbol.Create("ETHUSD", SecurityType.Crypto, Market.USA);
                yield return new TestCaseData(new MarketOrderTestParameters(ETHUSD));
                yield return new TestCaseData(new LimitOrderTestParameters(ETHUSD, 3600m, 3000m));
                yield return new TestCaseData(new StopLimitOrderTestParameters(ETHUSD, 3600m, 3000m)).Explicit("The WebSocket does not return an update order event, which is necessary for this test case to pass.");
            }
        }

        [Test, TestCaseSource(nameof(EquityOrderParameters))]
        public override void CancelOrders(OrderTestParameters parameters)
        {
            base.CancelOrders(parameters);
        }

        [Test, TestCaseSource(nameof(CryptoOrderParameters))]
        public void CancelOrdersCrypto(OrderTestParameters parameters)
        {
            base.CancelOrders(parameters);
        }

        [Test, TestCaseSource(nameof(EquityOrderParameters))]
        public override void LongFromZero(OrderTestParameters parameters)
        {
            base.LongFromZero(parameters);
        }

        [Test, TestCaseSource(nameof(CryptoOrderParameters))]
        public void LongFromZeroCrypto(OrderTestParameters parameters)
        {
            base.LongFromZero(parameters);
        }

        [Test, TestCaseSource(nameof(EquityOrderParameters))]
        public override void CloseFromLong(OrderTestParameters parameters)
        {
            base.CloseFromLong(parameters);
        }

        [Test, TestCaseSource(nameof(CryptoOrderParameters))]
        public void CloseFromLongCrypto(OrderTestParameters parameters)
        {
            base.CloseFromLong(parameters);
        }

        [Test, TestCaseSource(nameof(EquityOrderParameters))]
        public override void ShortFromZero(OrderTestParameters parameters)
        {
            base.ShortFromZero(parameters);
        }

        [Test, TestCaseSource(nameof(CryptoOrderParameters))]
        public void ShortFromZeroCrypto(OrderTestParameters parameters)
        {
            base.ShortFromZero(parameters);
        }

        [Test, TestCaseSource(nameof(EquityOrderParameters))]
        public override void CloseFromShort(OrderTestParameters parameters)
        {
            base.CloseFromShort(parameters);
        }

        [Test, TestCaseSource(nameof(EquityOrderParameters))]
        [Explicit("Not supported: Different side position if we have bought 1 quantity we can not sell more then 1")]
        public override void ShortFromLong(OrderTestParameters parameters)
        {
            base.ShortFromLong(parameters);
        }

        [Test, TestCaseSource(nameof(EquityOrderParameters))]
        [Explicit("Not supported: Different side position if we have sold -1 quantity we can not bought more then 1")]
        public override void LongFromShort(OrderTestParameters parameters)
        {
            base.LongFromShort(parameters);
        }

        [Test]
        public void UpdateNotExistOrder()
        {
            var limitOrder = new LimitOrder(Symbol, 1, 2000m, DateTime.UtcNow);
            limitOrder.BrokerId.Add(Guid.NewGuid().ToString());
            Assert.IsFalse(Brokerage.UpdateOrder(limitOrder));
        }

        [Test]
        public void LookupSymbols()
        {
            var option = Symbol.CreateCanonicalOption(Symbols.AAPL);

            var options = (Brokerage as IDataQueueUniverseProvider).LookupSymbols(option, false).ToList();
            Assert.IsNotNull(options);
            Assert.True(options.Any());
            Assert.Greater(options.Count, 0);
            Assert.That(options.Distinct().ToList().Count, Is.EqualTo(options.Count));
        }

        [Test, TestCaseSource(nameof(CryptoOrderParameters))]
        public void LongUpdateOrderCrypto(OrderTestParameters parameters)
        {
            Log.Trace("");
            Log.Trace("LONG UPDATE ORDER CRYPTO");
            Log.Trace("");

            var order = PlaceOrderWaitForStatus(parameters.CreateLongOrder(GetDefaultQuantity()), parameters.ExpectedStatus);

            if (parameters.ModifyUntilFilled)
            {
                ModifyOrderUntilFilled(order, parameters);
            }
        }
    }
}