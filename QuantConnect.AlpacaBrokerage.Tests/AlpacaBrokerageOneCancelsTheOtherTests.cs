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
using QuantConnect.Logging;
using QuantConnect.Orders;
using QuantConnect.Tests;
using QuantConnect.Tests.Brokerages;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using TradeEvent = Alpaca.Markets.TradeEvent;
using QuantConnect.Brokerages.Alpaca.Tests.Models;

namespace QuantConnect.Brokerages.Alpaca.Tests
{
    // Alpaca's OCO is exit-only, so every scenario below opens a long
    // position first, then submits the 2-leg exit group the same way
    // QCAlgorithm.OneCancelsTheOtherOrder/SubmitGroupOrder would: two plain orders sharing one
    // GroupOrderManager with GroupExecutionType.OneCancelsTheOther, built directly (there is no live
    // QCAlgorithm in this test fixture to call the public API through).
    public partial class AlpacaBrokerageTests
    {
        [Test]
        public void OneCancelsTheOtherOrderTakeProfitLegFillsAndCancelsStopLossLeg()
        {
            Log.Trace("");
            Log.Trace("ONE-CANCELS-THE-OTHER: TAKE PROFIT LEG WINS");
            Log.Trace("");

            PlaceOrderWaitForStatus(new MarketOrderTestParameters(Symbol).CreateLongOrder(GetDefaultQuantity()), OrderStatus.Filled);

            var currentPrice = GetAskPrice(Symbol);
            // a sell limit priced below the current ask crosses the spread and should fill almost
            // immediately; the stop is priced far away so it can never trigger during the test
            var takeProfitLimitPrice = Math.Round(currentPrice * 0.98m, 2);
            var stopLossStopPrice = Math.Round(currentPrice * 0.5m, 2);

            var orders = CreateOneCancelsTheOtherOrders(-GetDefaultQuantity(), takeProfitLimitPrice, stopLossStopPrice);
            PlaceOrderWaitForStatus(orders, OrderStatus.Submitted);
            AssertEachLegGotItsOwnBrokerId(orders);

            WaitForOneCancelsTheOtherResolution(orders);

            Assert.AreEqual(OrderStatus.Filled, orders.Single(o => o.Type == OrderType.Limit).Status);
            Assert.AreEqual(OrderStatus.Canceled, orders.Single(o => o.Type == OrderType.StopMarket).Status);
        }

        [Test]
        public void OneCancelsTheOtherOrderStopLossLegFillsAndCancelsTakeProfitLeg()
        {
            Log.Trace("");
            Log.Trace("ONE-CANCELS-THE-OTHER: STOP LOSS LEG WINS");
            Log.Trace("");

            PlaceOrderWaitForStatus(new MarketOrderTestParameters(Symbol).CreateLongOrder(GetDefaultQuantity()), OrderStatus.Filled);

            var currentPrice = GetAskPrice(Symbol);
            // a sell stop priced above the current price is already triggered and should fill almost
            // immediately; the limit is priced far away so it can never become marketable during the test
            var takeProfitLimitPrice = Math.Round(currentPrice * 1.5m, 2);
            var stopLossStopPrice = Math.Round(currentPrice * 1.02m, 2);

            var orders = CreateOneCancelsTheOtherOrders(-GetDefaultQuantity(), takeProfitLimitPrice, stopLossStopPrice);
            PlaceOrderWaitForStatus(orders, OrderStatus.Submitted);
            AssertEachLegGotItsOwnBrokerId(orders);

            WaitForOneCancelsTheOtherResolution(orders);

            Assert.AreEqual(OrderStatus.Canceled, orders.Single(o => o.Type == OrderType.Limit).Status);
            Assert.AreEqual(OrderStatus.Filled, orders.Single(o => o.Type == OrderType.StopMarket).Status);
        }

        [Test]
        public void OneCancelsTheOtherOrderCancelingOneLegCancelsWholeGroup()
        {
            Log.Trace("");
            Log.Trace("ONE-CANCELS-THE-OTHER: CANCELING ONE LEG CANCELS THE WHOLE GROUP");
            Log.Trace("");

             PlaceOrderWaitForStatus(new MarketOrderTestParameters(Symbol).CreateLongOrder(GetDefaultQuantity()), OrderStatus.Filled);

            var currentPrice = GetAskPrice(Symbol);
            // both legs are priced far away from the market so neither can fill before we cancel one of them
            var takeProfitLimitPrice = Math.Round(currentPrice * 1.5m, 2);
            var stopLossStopPrice = Math.Round(currentPrice * 0.5m, 2);

            var orders = CreateOneCancelsTheOtherOrders(-GetDefaultQuantity(), takeProfitLimitPrice, stopLossStopPrice);
            PlaceOrderWaitForStatus(orders, OrderStatus.Submitted);
            AssertEachLegGotItsOwnBrokerId(orders);

            using var bothCanceledEvent = new ManualResetEvent(false);
            EventHandler<List<OrderEvent>> handler = (_, __) =>
            {
                if (orders.All(o => o.Status == OrderStatus.Canceled))
                {
                    try { bothCanceledEvent.Set(); } catch (ObjectDisposedException) { }
                }
            };

            Brokerage.OrdersStatusChanged += handler;
            try
            {
                Assert.IsTrue(Brokerage.CancelOrder(orders.First()));
                bothCanceledEvent.WaitOneAssertFail(30000, "Expected both one-cancels-the-other legs to end up Canceled after canceling one of them." +
                    string.Join("", orders.Select(o => $" Order Id:{o.Id} Status:{o.Status}")));
            }
            finally
            {
                Brokerage.OrdersStatusChanged -= handler;
            }
        }

        [Test]
        public void HandleTradeUpdateEmitsCanceledForHeldLegWithNoPriorNewEvent()
        {
            // Regression test for a live-testing bug: the passive/held leg of a one-cancels-the-other group
            // never receives its own New/PendingNew event - Alpaca reports "held" instead, straight to
            // "canceled" once the other leg wins. Held used to be a pure no-op that did not register the
            // order in _duplicationExecutionOrderIdByBrokerageOrderId, so the later genuine Canceled event
            // found nothing to Remove(), was mistaken for an unregistered replay, and was silently dropped -
            // both legs stayed stuck at Submitted forever.
            var orderId = Guid.NewGuid();

            var order = new StopMarketOrder(Symbols.AAPL, -1m, 190m, default);
            order.BrokerId.Add(orderId.ToString());
            OrderProvider.Add(order);

            var held = new TestTradeUpdate(TradeEvent.Held, null, new TestOrder(orderId));
            var canceled = new TestTradeUpdate(TradeEvent.Canceled, null, new TestOrder(orderId));

            AlpacaBrokerage.HandleTradeUpdate(held);
            Assert.AreNotEqual(OrderStatus.Canceled, order.Status, "Held itself should not change the order's status.");

            var canceledEventReceived = false;
            EventHandler<List<OrderEvent>> handler = (_, orderEvents) =>
            {
                if (orderEvents[0].Status == OrderStatus.Canceled)
                {
                    canceledEventReceived = true;
                }
            };

            Brokerage.OrdersStatusChanged += handler;
            try
            {
                AlpacaBrokerage.HandleTradeUpdate(canceled);
            }
            finally
            {
                Brokerage.OrdersStatusChanged -= handler;
            }

            Assert.IsTrue(canceledEventReceived, "Expected a Canceled OrderEvent even though the order never received a New/PendingNew event first.");
        }

        /// <summary>
        /// Builds a plain 2-leg one-cancels-the-other group (one Limit take-profit leg, one StopMarket stop-loss
        /// leg) sharing one <see cref="GroupOrderManager"/>, mirroring what
        /// QCAlgorithm.OneCancelsTheOtherOrder/SubmitGroupOrder builds internally.
        /// </summary>
        private List<Order> CreateOneCancelsTheOtherOrders(decimal quantity, decimal takeProfitLimitPrice, decimal stopLossStopPrice)
        {
            var groupOrderManager = new GroupOrderManager(2, quantity, 0) { ExecutionType = GroupExecutionType.OneCancelsTheOther };

            var limitOrder = new LimitOrder(Symbol, quantity, takeProfitLimitPrice, DateTime.UtcNow, properties: OrderProperties)
            {
                GroupOrderManager = groupOrderManager,
                Status = OrderStatus.New
            };
            var stopOrder = new StopMarketOrder(Symbol, quantity, stopLossStopPrice, DateTime.UtcNow, properties: OrderProperties)
            {
                GroupOrderManager = groupOrderManager,
                Status = OrderStatus.New
            };

            return new List<Order> { limitOrder, stopOrder };
        }

        /// <summary>
        /// Waits until one leg of the group reaches Filled and the other reaches Canceled. Unlike
        /// <see cref="BrokerageTests.PlaceOrderWaitForStatus"/>, the two legs are expected to end up in
        /// different terminal statuses, so a single shared expected status cannot be used here.
        /// </summary>
        private void WaitForOneCancelsTheOtherResolution(IReadOnlyCollection<Order> orders, double secondsTimeout = 30.0)
        {
            bool IsResolved() => orders.Any(o => o.Status == OrderStatus.Filled) && orders.Any(o => o.Status == OrderStatus.Canceled);

            using var resolvedEvent = new ManualResetEvent(false);
            EventHandler<List<OrderEvent>> handler = (_, __) =>
            {
                if (IsResolved())
                {
                    try { resolvedEvent.Set(); } catch (ObjectDisposedException) { }
                }
            };

            Brokerage.OrdersStatusChanged += handler;
            try
            {
                if (!IsResolved())
                {
                    resolvedEvent.WaitOneAssertFail(Convert.ToInt32(1000 * secondsTimeout),
                        "Expected one leg of the one-cancels-the-other group to fill and the other to be canceled." +
                        string.Join("", orders.Select(o => $" Order Id:{o.Id} Status:{o.Status}")));
                }
            }
            finally
            {
                Brokerage.OrdersStatusChanged -= handler;
            }
        }

        /// <summary>
        /// Verifies that every leg of the group received its own, distinct Alpaca order id right after
        /// submission - the parent (take-profit) id from the OCO POST response and the child (stop-loss)
        /// id from its nested leg.
        /// </summary>
        private static void AssertEachLegGotItsOwnBrokerId(IReadOnlyCollection<Order> orders)
        {
            Assert.IsTrue(orders.All(o => o.BrokerId.Count > 0), "Expected every one-cancels-the-other leg to receive its own Alpaca order id.");
            Assert.AreEqual(orders.Count, orders.Select(o => o.BrokerId.Single()).Distinct().Count(),
                "Expected each one-cancels-the-other leg to receive a distinct Alpaca order id.");
        }
    }
}
