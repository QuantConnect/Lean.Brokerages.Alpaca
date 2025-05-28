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

using NUnit.Framework;
using QuantConnect.Interfaces;
using QuantConnect.Logging;
using QuantConnect.Orders;
using QuantConnect.Orders.TimeInForces;
using QuantConnect.Securities;
using QuantConnect.Tests;
using QuantConnect.Tests.Brokerages;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using static QuantConnect.Brokerages.Alpaca.Tests.AlpacaBrokerageAdditionalTests;

namespace QuantConnect.Brokerages.Alpaca.Tests
{
    [TestFixture]
    public partial class AlpacaBrokerageTests : BrokerageTests
    {
        protected override Symbol Symbol { get; } = Symbols.AAPL;
        protected override SecurityType SecurityType { get; }

        protected override BrokerageName BrokerageName => BrokerageName.Alpaca;

        protected override IBrokerage CreateBrokerage(IOrderProvider orderProvider, ISecurityProvider securityProvider)
        {
            var (apiKey, apiKeySecret, isPaperTrading, accessToken) = AlpacaBrokerageTestHelpers.GetConfigParameters();

            return new TestAlpacaBrokerage(apiKey, apiKeySecret, isPaperTrading, orderProvider, securityProvider, accessToken);
        }
        protected override bool IsAsync() => false;
        protected override decimal GetAskPrice(Symbol symbol)
        {
            return (Brokerage as TestAlpacaBrokerage).GetLatestQuotePublic(symbol).AskPrice;
        }

        /// <summary>
        /// Provides the data required to test each order type in various cases
        /// </summary>
        private static IEnumerable<TestCaseData> EquityOrderParameters
        {
            get
            {
                var EPU = Symbol.Create("AAPL", SecurityType.Equity, Market.USA);
                yield return new TestCaseData(new MarketOrderTestParameters(EPU));
                yield return new TestCaseData(new LimitOrderTestParameters(EPU, 250m, 200m));
                yield return new TestCaseData(new StopMarketOrderTestParameters(EPU, 250m, 200m));
                yield return new TestCaseData(new StopLimitOrderTestParameters(EPU, 250m, 200m));
            }
        }

        /// <summary>
        /// Provides the data required to test each order type in various cases
        /// </summary>
        private static IEnumerable<TestCaseData> OptionOrderParameters
        {
            get
            {

                var option = Symbol.CreateOption(Symbols.AAPL, Symbols.AAPL.ID.Market, OptionStyle.American, OptionRight.Call, 230, new DateTime(2024, 12, 20));
                yield return new TestCaseData(new MarketOrderTestParameters(option));
                yield return new TestCaseData(new LimitOrderTestParameters(option, 20m, 10m));

                // see https://docs.alpaca.markets/docs/options-trading-overview
                yield return new TestCaseData(new StopMarketOrderTestParameters(option, 20m, 10m)).Explicit("Not supported by alpaca");
                yield return new TestCaseData(new StopLimitOrderTestParameters(option, 20m, 10m)).Explicit("Not supported by alpaca");
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

        [Test]
        public void PartialCryptoFill()
        {
            var parameters = new MarketOrderTestParameters(Symbol.Create("BTCUSD", SecurityType.Crypto, Market.USA));

            PlaceOrderWaitForStatus(parameters.CreateLongOrder(2), parameters.ExpectedStatus);
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

        [Test, TestCaseSource(nameof(OptionOrderParameters))]
        public void CancelOrdersOption(OrderTestParameters parameters)
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

        [Test, TestCaseSource(nameof(OptionOrderParameters))]
        public void LongFromZeroOption(OrderTestParameters parameters)
        {
            base.LongFromZero(parameters);
        }

        [TestCase(100)]
        public void PlaceLongThenShortMarketOrderMultipleTime(int amountOfTrade)
        {
            var GLD = Symbol.Create("GLD", SecurityType.Equity, Market.USA);
            var parameters = new MarketOrderTestParameters(GLD);
            var resetEvent = new ManualResetEvent(false);

            Brokerage.OrdersStatusChanged += (object _, List<OrderEvent> orderEvents) =>
            {
                var orderEvent = orderEvents[0];

                if (orderEvent.Status == OrderStatus.Filled)
                {
                    resetEvent.Set();
                }
            };

            var switcher = default(bool);
            do
            {
                if (!switcher)
                {
                    switcher = true;
                    PlaceOrderWaitForStatus(parameters.CreateLongOrder(100), parameters.ExpectedStatus);
                }
                else
                {
                    switcher = false;
                    PlaceOrderWaitForStatus(parameters.CreateShortMarketOrder(100), parameters.ExpectedStatus);
                }

                resetEvent.WaitOne(TimeSpan.FromSeconds(60));
                resetEvent.Reset();

            } while (amountOfTrade-- > 0);
        }

        [Test, TestCaseSource(nameof(EquityOrderParameters))]
        public override void CloseFromLong(OrderTestParameters parameters)
        {
            base.CloseFromLong(parameters);
        }

        [Test, TestCaseSource(nameof(OptionOrderParameters))]
        public void CloseFromLongOption(OrderTestParameters parameters)
        {
            base.CloseFromLong(parameters);
        }

        [Test, TestCaseSource(nameof(CryptoOrderParameters))]
        public void CloseFromLongCrypto(OrderTestParameters parameters)
        {
            Log.Trace("");
            Log.Trace("CLOSE FROM LONG");
            Log.Trace("");
            PlaceOrderWaitForStatus(parameters.CreateLongMarketOrder(GetDefaultQuantity()), OrderStatus.Filled);

            Log.Trace("");
            Log.Trace("GET ACCOUNT HOLDINGS");
            Log.Trace("");
            foreach (var accountHolding in Brokerage.GetAccountHoldings())
            {
                if (SecurityProvider.TryGetValue(accountHolding.Symbol, out var holding))
                {
                    holding.Holdings.SetHoldings(accountHolding.AveragePrice, accountHolding.Quantity);
                }
            }

            var actualOrderQuantity = SecurityProvider[parameters.Symbol].Holdings.Quantity;

            PlaceOrderWaitForStatus(parameters.CreateShortOrder(actualOrderQuantity), parameters.ExpectedStatus);
        }

        [Test, TestCaseSource(nameof(EquityOrderParameters))]
        public override void ShortFromZero(OrderTestParameters parameters)
        {
            base.ShortFromZero(parameters);
        }

        [Test, TestCaseSource(nameof(EquityOrderParameters))]
        public override void CloseFromShort(OrderTestParameters parameters)
        {
            base.CloseFromShort(parameters);
        }

        [Test, TestCaseSource(nameof(EquityOrderParameters))]
        public override void ShortFromLong(OrderTestParameters parameters)
        {
            base.ShortFromLong(parameters);
        }

        [Test, TestCaseSource(nameof(EquityOrderParameters))]
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

        private static IEnumerable<TestCaseData> MarketOpenCloseOrderTypeParameters
        {
            get
            {
                var symbol = Symbols.AAPL;
                yield return new TestCaseData(new MarketOnOpenOrder(symbol, 1m, DateTime.UtcNow), !symbol.IsMarketOpen(DateTime.UtcNow, false));
                yield return new TestCaseData(new MarketOnCloseOrder(symbol, 1m, DateTime.UtcNow), symbol.IsMarketOpen(DateTime.UtcNow, false));
            }
        }

        [TestCaseSource(nameof(MarketOpenCloseOrderTypeParameters))]
        public void PlaceMarketOpenCloseOrder(Order order, bool marketIsOpen)
        {
            Log.Trace($"PLACE {order.Type} ORDER TEST");

            var submittedResetEvent = new AutoResetEvent(false);
            var invalidResetEvent = new AutoResetEvent(false);

            OrderProvider.Add(order);

            Brokerage.OrdersStatusChanged += (_, orderEvents) =>
            {
                var orderEvent = orderEvents[0];

                Log.Trace("");
                Log.Trace($"{nameof(PlaceMarketOpenCloseOrder)}.OrderEvent.Status: {orderEvent.Status}");
                Log.Trace("");

                if (orderEvent.Status == OrderStatus.Submitted)
                {
                    submittedResetEvent.Set();
                }
                else if (orderEvent.Status == OrderStatus.Invalid)
                {
                    invalidResetEvent.Set();
                }
            };



            if (marketIsOpen)
            {
                Assert.IsTrue(Brokerage.PlaceOrder(order));

                if (!submittedResetEvent.WaitOne(TimeSpan.FromSeconds(5)))
                {
                    Assert.Fail($"{nameof(PlaceMarketOpenCloseOrder)}: the brokerage doesn't return {OrderStatus.Submitted}");
                }

                var openOrders = Brokerage.GetOpenOrders();

                Assert.IsNotEmpty(openOrders);
                Assert.That(openOrders.Count, Is.EqualTo(1));
                Assert.That(openOrders[0].Type, Is.EqualTo(order.Type));
                Assert.IsTrue(Brokerage.CancelOrder(order));
            }
            else
            {
                Assert.IsFalse(Brokerage.PlaceOrder(order));

                if (!invalidResetEvent.WaitOne(TimeSpan.FromSeconds(5)))
                {
                    Assert.Fail($"{nameof(PlaceMarketOpenCloseOrder)}: the brokerage doesn't return {OrderStatus.Invalid}");
                }
            }
        }


        private static IEnumerable<OrderTestParameters> OrdersParameters
        {
            get
            {
                var symbol = Symbol.Create("AAPL", SecurityType.Equity, Market.USA);
                var orderProperties = new AlpacaOrderProperties() { OutsideRegularTradingHours = true };

                yield return new MarketOrderTestParameters(symbol, properties: orderProperties);
                yield return new LimitOrderTestParameters(symbol, 250m, 150m, properties: orderProperties);
                yield return new StopMarketOrderTestParameters(symbol, 250m, 150m, properties: orderProperties);
                yield return new StopLimitOrderTestParameters(symbol, 250m, 150m, properties: orderProperties);
            }
        }

        private static IEnumerable<TimeInForce> TimeInForceCases
        {
            get
            {
                yield return TimeInForce.Day;
                yield return TimeInForce.GoodTilCanceled;
                yield return TimeInForce.GoodTilDate(DateTime.UtcNow.AddDays(7)); // Not supported whatsoever
            }
        }

        [Test]
        public void CannotPlaceOutsideRegularHoursOrder(
            [ValueSource(nameof(OrdersParameters))] OrderTestParameters ordersParameters,
            [ValueSource(nameof(TimeInForceCases))] TimeInForce timeInForce)
        {
            if (ordersParameters is LimitOrderTestParameters && timeInForce is DayTimeInForce)
            {
                Assert.Ignore("Limit orders with Day time in force are allowed outside regular trading hours.");
            }

            var order = ordersParameters.CreateLongOrder(GetDefaultQuantity());
            order.Properties.TimeInForce = timeInForce;

            var invalidOrderEvent = new AutoResetEvent(false);
            Brokerage.OrdersStatusChanged += (_, orderEvents) =>
            {
                var orderEvent = orderEvents[0];
                if (orderEvent.Status == OrderStatus.Invalid)
                {
                    Console.WriteLine(orderEvent.ToString());
                    invalidOrderEvent.Set();
                }
            };

            OrderProvider.Add(order);
            Assert.IsTrue(Brokerage.PlaceOrder(order));
            Assert.IsTrue(invalidOrderEvent.WaitOne(TimeSpan.FromSeconds(5)));
        }

        [Test]
        public void PlaceOutsideRegularHoursLimitOrders()
        {
            var symbol = Symbol.Create("AAPL", SecurityType.Equity, Market.USA);
            var orderProperties = new AlpacaOrderProperties()
            {
                OutsideRegularTradingHours = true,
                TimeInForce = TimeInForce.Day
            };
            var limitOrder = new LimitOrder(symbol, 1, 190m, DateTime.UtcNow, properties: orderProperties);

            var submittedOrderEvent = new AutoResetEvent(false);
            Brokerage.OrdersStatusChanged += (_, orderEvents) =>
            {
                var orderEvent = orderEvents[0];
                if (orderEvent.Status == OrderStatus.Submitted)
                {
                    Console.WriteLine(orderEvent.ToString());
                    submittedOrderEvent.Set();
                }
            };

            Assert.IsTrue(Brokerage.PlaceOrder(limitOrder));
            Assert.IsTrue(submittedOrderEvent.WaitOne(TimeSpan.FromSeconds(5)));
        }
    }
}