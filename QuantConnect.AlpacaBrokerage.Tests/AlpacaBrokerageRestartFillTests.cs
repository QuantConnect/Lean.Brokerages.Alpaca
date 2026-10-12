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
using System.Reflection;
using NUnit.Framework;
using System.Runtime.CompilerServices;
using QuantConnect.Orders;
using QuantConnect.Securities;
using QuantConnect.Tests;
using QuantConnect.Tests.Brokerages;
using System.Collections.Generic;
using QuantConnect.Brokerages.Alpaca.Tests.Models;
using TradeEvent = global::Alpaca.Markets.TradeEvent;
using OrderSide = global::Alpaca.Markets.OrderSide;
using static QuantConnect.Brokerages.Alpaca.Tests.AlpacaBrokerageAdditionalTests;

namespace QuantConnect.Brokerages.Alpaca.Tests
{
    /// <summary>
    /// Fill accounting of an order Alpaca had already partially filled before the algorithm restarted.
    /// The order comes back through <see cref="AlpacaBrokerage.GetOpenOrders"/> with its filled quantity,
    /// the engine loads the holdings that quantity produced from the account, and the next trade update
    /// reports the cumulative filled quantity. Only the quantity filled since the restart is new.
    /// No connection is needed: the trade updates are fed to the handler directly.
    /// </summary>
    [TestFixture]
    public class AlpacaBrokerageRestartFillTests
    {
        private static readonly Security _security = TestsHelpers.GetSecurity(symbol: "AAPL", securityType: SecurityType.Equity, market: Market.USA);

        /// <summary>
        /// Builds the brokerage without running its constructor, which needs Alpaca credentials to list the crypto assets,
        /// and gives every collection field the empty instance its initializer would have created. The trade update
        /// handler needs nothing else: no client, no stream, no connection.
        /// </summary>
        private static (TestAlpacaBrokerage brokerage, OrderProvider orderProvider) CreateBrokerage()
        {
            var orderProvider = new OrderProvider(new List<Order>());
            var securityProvider = new SecurityProvider(new Dictionary<Symbol, Security> { { _security.Symbol, _security } });

            var brokerage = (TestAlpacaBrokerage)RuntimeHelpers.GetUninitializedObject(typeof(TestAlpacaBrokerage));
            for (var type = typeof(TestAlpacaBrokerage); type != null; type = type.BaseType)
            {
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (field.FieldType.IsClass && !field.FieldType.IsAbstract && field.FieldType != typeof(string)
                        && !typeof(Delegate).IsAssignableFrom(field.FieldType)
                        && field.FieldType.GetConstructor(Type.EmptyTypes) != null && field.GetValue(brokerage) == null)
                    {
                        field.SetValue(brokerage, Activator.CreateInstance(field.FieldType));
                    }
                }
            }
            var symbolMapper = (AlpacaBrokerageSymbolMapper)RuntimeHelpers.GetUninitializedObject(typeof(AlpacaBrokerageSymbolMapper));
            SetField(symbolMapper, "_brokerageSymbolByLeanSymbol", new Dictionary<string, string>());
            SetField(brokerage, "_symbolMapper", symbolMapper);
            SetField(brokerage, "_orderProvider", orderProvider);
            SetField(brokerage, "_securityProvider", securityProvider);
            return (brokerage, orderProvider);
        }

        private static void SetField(object target, string name, object value)
        {
            for (var type = target.GetType(); type != null; type = type.BaseType)
            {
                var field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field != null)
                {
                    field.SetValue(target, value);
                    return;
                }
            }
            throw new MissingFieldException(target.GetType().Name, name);
        }

        /// <summary>
        /// Imports the order the way <see cref="AlpacaBrokerage.GetOpenOrders"/> does, for an Alpaca limit order with
        /// filled_qty 1 of qty 3, and registers it with the order provider as the engine does for an open order.
        /// </summary>
        private static Order AddImportedPartiallyFilledOrder(TestAlpacaBrokerage brokerage, OrderProvider orderProvider, Guid brokerageOrderId)
        {
            var brokerageOrder = new TestOrder(brokerageOrderId, 1m, OrderSide.Buy)
            {
                Quantity = 3m,
                OrderType = global::Alpaca.Markets.OrderType.Limit,
                LimitPrice = 1m,
                OrderClass = global::Alpaca.Markets.OrderClass.Simple,
                TimeInForce = global::Alpaca.Markets.TimeInForce.Gtc,
                OrderStatus = global::Alpaca.Markets.OrderStatus.PartiallyFilled,
                SubmittedAtUtc = new DateTime(2026, 1, 2, 14, 30, 0, DateTimeKind.Utc)
            };
            Assert.IsTrue(brokerage.TryConvertToLeanOrders(brokerageOrder, out var leanOrders));
            var order = leanOrders[0];
            Assert.AreEqual(OrderStatus.PartiallyFilled, order.Status);
            Assert.AreEqual(_security.Symbol, order.Symbol);
            orderProvider.Add(order);
            return order;
        }

        [TestCase(TradeEvent.Fill, 3, 2)]
        [TestCase(TradeEvent.PartialFill, 2, 1)]
        public void FillAfterRestartReportsOnlyTheQuantityFilledSinceTheRestart(TradeEvent tradeEvent, decimal cumulativeFilledQuantity, decimal expectedFillQuantity)
        {
            // Alpaca filled 1 of 3 before the restart; that share is already in the holdings loaded from the account
            var brokerageOrderId = Guid.NewGuid();
            var (brokerage, orderProvider) = CreateBrokerage();
            {
                AddImportedPartiallyFilledOrder(brokerage, orderProvider, brokerageOrderId);

                var orderEvents = new List<OrderEvent>();
                brokerage.OrdersStatusChanged += (_, events) => orderEvents.AddRange(events);

                brokerage.HandleTradeUpdate(new TestTradeUpdate(tradeEvent, Guid.NewGuid(), new TestOrder(brokerageOrderId, cumulativeFilledQuantity, OrderSide.Buy) { Quantity = 3m }));

                Assert.AreEqual(1, orderEvents.Count);
                Assert.AreEqual(tradeEvent == TradeEvent.Fill ? OrderStatus.Filled : OrderStatus.PartiallyFilled, orderEvents[0].Status);
                Assert.AreEqual(expectedFillQuantity, orderEvents[0].FillQuantity);
            }
        }

        [Test]
        public void FillOfAnOrderPlacedAfterTheRestartReportsTheWholeQuantity()
        {
            // the same sequence on an order Lean placed itself: the cumulative quantity is all new
            var brokerageOrderId = Guid.NewGuid();
            var (brokerage, orderProvider) = CreateBrokerage();
            {
                var order = new LimitOrder(_security.Symbol, 3m, 1m, default);
                order.BrokerId.Add(brokerageOrderId.ToString());
                orderProvider.Add(order);

                var orderEvents = new List<OrderEvent>();
                brokerage.OrdersStatusChanged += (_, events) => orderEvents.AddRange(events);

                brokerage.HandleTradeUpdate(new TestTradeUpdate(TradeEvent.New, Guid.NewGuid(), new TestOrder(brokerageOrderId)));
                brokerage.HandleTradeUpdate(new TestTradeUpdate(TradeEvent.Fill, Guid.NewGuid(), new TestOrder(brokerageOrderId, 3m, OrderSide.Buy) { Quantity = 3m }));

                Assert.AreEqual(1, orderEvents.Count);
                Assert.AreEqual(3m, orderEvents[0].FillQuantity);
            }
        }
    }
}
