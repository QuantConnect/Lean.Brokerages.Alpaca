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

using Moq;
using NUnit.Framework;
using QuantConnect.Orders;
using QuantConnect.Tests;
using QuantConnect.Tests.Brokerages;
using System;
using System.Collections.Generic;

namespace QuantConnect.Brokerages.Alpaca.Tests;

[TestFixture]
public class AlpacaBrokerageExtensionsTests
{
    [TestCase(OrderType.ComboLimit, 1, Description = "Alpaca needs at least 2 legs")]
    [TestCase(OrderType.ComboLimit, 5, Description = "Alpaca takes at most 4 legs")]
    [TestCase(OrderType.ComboLegLimit, 2, Description = "Alpaca has no limit price per leg")]
    public void CreateAlpacaMultiLegOrderWhenOrderIsNotSupportedThrows(OrderType leanOrderType, int legCount)
    {
        // Arrange
        var groupOrderManager = new GroupOrderManager(legCount, quantity: 1m, limitPrice: 1m);
        var orders = new List<Order>(legCount);
        for (var i = 0; i < legCount; i++)
        {
            // legs alternate between buy and sell
            var legQuantity = i % 2 == 0 ? 1m : -1m;
            var symbol = Symbol.CreateOption(Symbols.AAPL, Market.USA, OptionStyle.American, OptionRight.Call, 250m + 5m * i, new DateTime(2026, 12, 18));
            switch (leanOrderType)
            {
                case OrderType.ComboLimit:
                    orders.Add(new ComboLimitOrder(symbol, legQuantity, 1m, new DateTime(2026, 9, 21), groupOrderManager));
                    break;
                case OrderType.ComboLegLimit:
                    orders.Add(new ComboLegLimitOrder(symbol, legQuantity, 1m, new DateTime(2026, 9, 21), groupOrderManager));
                    break;
                default:
                    throw new ArgumentException($"Unexpected combo order type '{leanOrderType}'.", nameof(leanOrderType));
            }
        }

        var symbolMapper = new Mock<ISymbolMapper>();
        symbolMapper.Setup(mapper => mapper.GetBrokerageSymbol(It.IsAny<Symbol>())).Returns<Symbol>(symbol => symbol.Value);

        // Act & Assert
        Assert.That(() => orders.CreateAlpacaMultiLegOrder(symbolMapper.Object, new SecurityProvider()), Throws.TypeOf<NotSupportedException>());
    }
}
