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
using NUnit.Framework;
using QuantConnect.Tests;
using QuantConnect.Orders;
using AlpacaMarket = Alpaca.Markets;

namespace QuantConnect.Brokerages.Alpaca.Tests
{
    /// <summary>
    /// Hermetic tests of the contingent orders support through the bracket, oco and oto order classes
    /// </summary>
    [TestFixture]
    public class AlpacaBrokerageContingentOrdersTests
    {
        [TestCase(AlpacaMarket.OrderStatus.Held, true)]
        [TestCase(AlpacaMarket.OrderStatus.New, true)]
        [TestCase(AlpacaMarket.OrderStatus.Accepted, true)]
        [TestCase(AlpacaMarket.OrderStatus.PartiallyFilled, true)]
        [TestCase(AlpacaMarket.OrderStatus.Filled, false)]
        [TestCase(AlpacaMarket.OrderStatus.Canceled, false)]
        [TestCase(AlpacaMarket.OrderStatus.Expired, false)]
        [TestCase(AlpacaMarket.OrderStatus.Rejected, false)]
        public void HeldOrdersAreOpen(AlpacaMarket.OrderStatus status, bool expected)
        {
            Assert.AreEqual(expected, AlpacaBrokerage.IsOpen(status));
        }

        [Test]
        public void AdvancedOrdersKeepTheLeanOrderSettings()
        {
            var order = new LimitOrder(Symbols.SPY, 10, 100, new DateTime(2024, 1, 2), properties: new AlpacaOrderProperties { TimeInForce = TimeInForce.Day, OutsideRegularTradingHours = true });
            var request = AlpacaMarket.LimitOrder.Buy("SPY", AlpacaMarket.OrderQuantity.Fractional(10), 100).Bracket(110, 90).WithLeanOrderSettings(order);

            Assert.AreEqual(AlpacaMarket.TimeInForce.Day, request.Duration);
            Assert.IsTrue(request.ExtendedHours == true);
            Assert.AreEqual(AlpacaMarket.OrderClass.Bracket, ((AlpacaMarket.BracketOrder)request).OrderClass);
        }
    }
}
