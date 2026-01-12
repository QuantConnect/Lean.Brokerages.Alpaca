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
using TradeEvent = Alpaca.Markets.TradeEvent;
using QuantConnect.Brokerages.Alpaca.Tests.Models;
using static QuantConnect.Brokerages.Alpaca.Tests.AlpacaBrokerageAdditionalTests;

namespace QuantConnect.Brokerages.Alpaca.Tests
{
    [TestFixture]
    public partial class AlpacaBrokerageTests : BrokerageTests
    {
        private TestAlpacaBrokerage AlpacaBrokerage => Brokerage as TestAlpacaBrokerage;

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
            return AlpacaBrokerage.GetLatestQuotePublic(symbol).AskPrice;
        }

        /// <summary>
        /// Provides the data required to test each order type in various cases
        /// </summary>
        private static IEnumerable<TestCaseData> EquityOrderParameters
        {
            get
            {
                var AAPL = Symbol.Create("AAPL", SecurityType.Equity, Market.USA);
                yield return new TestCaseData(new MarketOrderTestParameters(AAPL));
                yield return new TestCaseData(new LimitOrderTestParameters(AAPL, 280m, 250m));
                yield return new TestCaseData(new StopMarketOrderTestParameters(AAPL, 280m, 250m));
                yield return new TestCaseData(new StopLimitOrderTestParameters(AAPL, 280m, 250m));
                var CIFR = Symbol.Create("CIFR", SecurityType.Equity, Market.USA);
                yield return new TestCaseData(new TrailingStopOrderTestParameters(CIFR, 280m, 250m, 0.02m, trailingAsPercentage: true));
                var TSLA = Symbol.Create("TSLA", SecurityType.Equity, Market.USA);
                yield return new TestCaseData(new TrailingStopOrderTestParameters(TSLA, 470m, 440m, 5m, trailingAsPercentage: false));
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

        [Test]
        public void HandleTradeUpdateNormalCase()
        {
            // pending_new -> new -> partial_fill -> fill
            // the same to all trade updates
            var orderId = Guid.NewGuid();

            var order = new LimitOrder(Symbols.AAPL, 3m, 1m, default);
            order.BrokerId.Add(orderId.ToString());
            OrderProvider.Add(order);

            var tradeUpdates = new List<TestTradeUpdate>(4)
            {
                new(TradeEvent.PendingNew, null, new TestOrder(orderId)),
                new(TradeEvent.New, Guid.NewGuid(), new TestOrder(orderId)),
                new(TradeEvent.PartialFill, Guid.NewGuid(), new TestOrder(orderId, 1)),
                new(TradeEvent.Fill, Guid.NewGuid(), new TestOrder(orderId, 3))
            };

            foreach (var tradeUpdate in tradeUpdates)
            {
                AlpacaBrokerage.HandleTradeUpdate(tradeUpdate);

                switch (tradeUpdate.Event)
                {
                    case TradeEvent.PendingNew:
                    case TradeEvent.New:
                        Assert.AreEqual(1, AlpacaBrokerage._duplicationExecutionOrderIdByBrokerageOrderId.Count);
                        break;
                    case TradeEvent.PartialFill:
                        Assert.AreEqual(1, AlpacaBrokerage._duplicationExecutionOrderIdByBrokerageOrderId.Count);
                        Assert.AreEqual(1, AlpacaBrokerage._duplicationExecutionOrderIdByBrokerageOrderId[orderId].Count);
                        break;
                    case TradeEvent.Fill:
                        Assert.AreEqual(0, AlpacaBrokerage._duplicationExecutionOrderIdByBrokerageOrderId.Count);
                        break;
                }
            }            
        }

        [Test]
        public void HandleTradeUpdateHandleExpiredOrder()
        {
            // pending_new -> new -> expired
            // the same to all trade updates
            var orderId = Guid.NewGuid();

            var aop = new AlpacaOrderProperties()
            {
                TimeInForce = TimeInForce.Day
            };

            var order = new LimitOrder(Symbols.AAPL, 3m, 1m, default, properties: aop);
            order.BrokerId.Add(orderId.ToString());

            OrderProvider.Add(order);

            var tradeUpdates = new List<TestTradeUpdate>()
            {
                new(TradeEvent.PendingNew, null, new TestOrder(orderId)),
                new(TradeEvent.New, Guid.NewGuid(), new TestOrder(orderId))
            };

            var expiredExecutionId_1 = Guid.NewGuid();
            var expired_1 = new TestTradeUpdate(TradeEvent.Expired, expiredExecutionId_1, new TestOrder(orderId, 1));

            tradeUpdates.Add(expired_1);
            tradeUpdates.Add(expired_1);

            var orderWasCancelled = false;
            var cancelledCounter = 0;
            void HandleTradeUpdateHandleExpiredOrder(object _, List<OrderEvent> oes)
            {
                var oe = oes[0];
                switch (oe.Status)
                {
                    case OrderStatus.Canceled:
                        orderWasCancelled = true;
                        cancelledCounter += 1;
                        Assert.True(oe.Message.Contains("The order was canceled by the brokerage.", StringComparison.InvariantCultureIgnoreCase));
                        break;
                }
            }

            Brokerage.OrdersStatusChanged += HandleTradeUpdateHandleExpiredOrder;

            foreach (var tradeUpdate in tradeUpdates)
            {
                AlpacaBrokerage.HandleTradeUpdate(tradeUpdate);

                switch (tradeUpdate.Event)
                {
                    case TradeEvent.PendingNew:
                    case TradeEvent.New:
                        Assert.AreEqual(1, AlpacaBrokerage._duplicationExecutionOrderIdByBrokerageOrderId.Count);
                        break;
                    case TradeEvent.Expired when tradeUpdate.ExecutionId.Equals(expiredExecutionId_1):
                        Assert.AreEqual(0, AlpacaBrokerage._duplicationExecutionOrderIdByBrokerageOrderId.Count);
                        break;
                }
            }

            Brokerage.OrdersStatusChanged -= HandleTradeUpdateHandleExpiredOrder;

            Assert.True(orderWasCancelled);
            Assert.AreEqual(1, cancelledCounter);
            Assert.AreEqual(OrderStatus.Canceled, order.Status);
        }

        [Test]
        public void HandleTradeUpdateShouldSkipDuplication()
        {
            // pending_new -> new -> partial_fill -> fill
            // the same to all trade updates
            var orderId = Guid.NewGuid();

            var order = new LimitOrder(Symbols.AAPL, 3m, 1m, default);
            order.BrokerId.Add(orderId.ToString());

            OrderProvider.Add(order);

            var tradeUpdates = new List<TestTradeUpdate>()
            {
                new(TradeEvent.PendingNew, null, new TestOrder(orderId)),
                new(TradeEvent.New, Guid.NewGuid(), new TestOrder(orderId))
            };

            var partialFillExecutionId_1 = Guid.NewGuid();
            var partialFill_1 = new TestTradeUpdate(TradeEvent.PartialFill, partialFillExecutionId_1, new TestOrder(orderId, 1));

            tradeUpdates.Add(partialFill_1);
            tradeUpdates.Add(partialFill_1);

            var partialFillExecutionId_2 = Guid.NewGuid();
            var partialFill_2 = new TestTradeUpdate(TradeEvent.PartialFill, partialFillExecutionId_2, new TestOrder(orderId, 2));

            tradeUpdates.Add(partialFill_2);
            tradeUpdates.Add(partialFill_2);

            var fillExecutionId = Guid.NewGuid();
            var fill = new TestTradeUpdate(TradeEvent.Fill, fillExecutionId, new TestOrder(orderId, 3));

            tradeUpdates.Add(fill);
            tradeUpdates.Add(fill);

            foreach (var tradeUpdate in tradeUpdates)
            {
                AlpacaBrokerage.HandleTradeUpdate(tradeUpdate);

                switch (tradeUpdate.Event)
                {
                    case TradeEvent.PendingNew:
                    case TradeEvent.New:
                        Assert.AreEqual(1, AlpacaBrokerage._duplicationExecutionOrderIdByBrokerageOrderId.Count);
                        break;
                    case TradeEvent.PartialFill when tradeUpdate.ExecutionId.Equals(partialFillExecutionId_1):
                        Assert.AreEqual(1, AlpacaBrokerage._duplicationExecutionOrderIdByBrokerageOrderId.Count);
                        Assert.AreEqual(1, AlpacaBrokerage._duplicationExecutionOrderIdByBrokerageOrderId[orderId].Count);
                        break;
                    case TradeEvent.PartialFill when tradeUpdate.ExecutionId.Equals(partialFillExecutionId_2):
                        Assert.AreEqual(1, AlpacaBrokerage._duplicationExecutionOrderIdByBrokerageOrderId.Count);
                        Assert.AreEqual(2, AlpacaBrokerage._duplicationExecutionOrderIdByBrokerageOrderId[orderId].Count);
                        break;
                    case TradeEvent.Fill when tradeUpdate.ExecutionId.Equals(fillExecutionId):
                        Assert.AreEqual(0, AlpacaBrokerage._duplicationExecutionOrderIdByBrokerageOrderId.Count);
                        break;
                }
            }
        }

        [Test]
        public void HandleTradeUpdateShouldSkipCanceledDuplication()
        {
            // pending_new -> new -> partial_fill -> fill
            // the same to all trade updates
            var orderId = Guid.NewGuid();

            var order = new LimitOrder(Symbols.AAPL, 3m, 1m, default);
            order.BrokerId.Add(orderId.ToString());
            OrderProvider.Add(order);

            var tradeUpdates = new List<TestTradeUpdate>()
            {
                new(TradeEvent.PendingNew, null, new TestOrder(orderId)),
                new(TradeEvent.New, Guid.NewGuid(), new TestOrder(orderId))
            };

            var pendingCancelExecutionId = Guid.NewGuid();
            var pendingCancel = new TestTradeUpdate(TradeEvent.PendingCancel, pendingCancelExecutionId, new TestOrder(orderId, 1));

            tradeUpdates.Add(pendingCancel);
            tradeUpdates.Add(pendingCancel);

            var cancelExecutionId = Guid.NewGuid();
            var cancel = new TestTradeUpdate(TradeEvent.Canceled, cancelExecutionId, new TestOrder(orderId, 1));

            tradeUpdates.Add(cancel);
            tradeUpdates.Add(cancel);

            foreach (var tradeUpdate in tradeUpdates)
            {
                AlpacaBrokerage.HandleTradeUpdate(tradeUpdate);

                switch (tradeUpdate.Event)
                {
                    case TradeEvent.PendingNew:
                    case TradeEvent.New:
                        Assert.AreEqual(1, AlpacaBrokerage._duplicationExecutionOrderIdByBrokerageOrderId.Count);
                        Assert.AreEqual(0, AlpacaBrokerage._duplicationExecutionOrderIdByBrokerageOrderId[orderId].Count);
                        break;
                    case TradeEvent.PendingCancel:
                        Assert.AreEqual(1, AlpacaBrokerage._duplicationExecutionOrderIdByBrokerageOrderId.Count);
                        Assert.AreEqual(0, AlpacaBrokerage._duplicationExecutionOrderIdByBrokerageOrderId[orderId].Count);
                        break;
                    case TradeEvent.Canceled:
                        Assert.AreEqual(0, AlpacaBrokerage._duplicationExecutionOrderIdByBrokerageOrderId.Count);
                        break;
                }
            }
        }

        [Test]
        public void HandleTradeUpdateShouldSkipUpdateDuplication()
        {
            // pending_new -> new -> replaced -> canceled
            // the same to all trade updates
            var orderId = Guid.NewGuid();

            var order = new LimitOrder(Symbols.AAPL, 3m, 1m, default);
            order.BrokerId.Add(orderId.ToString());
            OrderProvider.Add(order);

            // Call: GetOpenOrders() has added already order
            AlpacaBrokerage._duplicationExecutionOrderIdByBrokerageOrderId[orderId] = [];

            var tradeUpdates = new List<TestTradeUpdate>()
            {
            };

            // Call: UpdateOrder
            var oldOrderId = orderId;
            orderId = Guid.NewGuid();
            order.BrokerId.Add(orderId.ToString());

            var replacedExecutionId = Guid.NewGuid();
            var replaced = new TestTradeUpdate(TradeEvent.Replaced, replacedExecutionId, new TestOrder(oldOrderId, 0) { ReplacedByOrderId = orderId });

            tradeUpdates.Add(replaced);
            tradeUpdates.Add(replaced);

            var pendingCancelExecutionId = Guid.NewGuid();
            var pendingCancel = new TestTradeUpdate(TradeEvent.PendingCancel, pendingCancelExecutionId, new TestOrder(orderId, 1));

            tradeUpdates.Add(pendingCancel);
            tradeUpdates.Add(pendingCancel);

            var cancelExecutionId = Guid.NewGuid();
            var cancel = new TestTradeUpdate(TradeEvent.Canceled, cancelExecutionId, new TestOrder(orderId, 1));

            tradeUpdates.Add(cancel);
            tradeUpdates.Add(cancel);

            foreach (var tradeUpdate in tradeUpdates)
            {
                AlpacaBrokerage.HandleTradeUpdate(tradeUpdate);

                switch (tradeUpdate.Event)
                {
                    case TradeEvent.Replaced:
                        Assert.AreEqual(1, AlpacaBrokerage._duplicationExecutionOrderIdByBrokerageOrderId.Count);
                        Assert.AreEqual(0, AlpacaBrokerage._duplicationExecutionOrderIdByBrokerageOrderId[orderId].Count);
                        break;
                    case TradeEvent.Canceled:
                        Assert.AreEqual(0, AlpacaBrokerage._duplicationExecutionOrderIdByBrokerageOrderId.Count);
                        break;
                }
            }
        }
    }
}