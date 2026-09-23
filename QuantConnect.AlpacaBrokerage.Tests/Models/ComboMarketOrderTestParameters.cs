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
using QuantConnect.Orders;
using QuantConnect.Interfaces;
using System.Collections.Generic;
using QuantConnect.Tests.Brokerages;
using QuantConnect.Securities.Option;

namespace QuantConnect.Brokerages.Alpaca.Tests.Models;

/// <summary>
/// Provides test parameters and helper methods for creating combo market orders.
/// The market twin of <see cref="ComboLimitOrderTestParameters"/>.
/// </summary>
public class ComboMarketOrderTestParameters
{
    private readonly string _name;
    private readonly List<Leg> _legs;
    private readonly IOrderProperties _orderProperties;

    /// <summary>
    /// The status to expect when submitting this order in most test cases.
    /// </summary>
    public OrderStatus ExpectedStatus => OrderStatus.Filled;

    /// <summary>
    /// Initializes a new instance of the <see cref="ComboMarketOrderTestParameters"/> class.
    /// </summary>
    /// <param name="strategy">The Specification of the option strategy to trade.</param>
    /// <param name="orderProperties">Optional order properties to attach to each order.</param>
    public ComboMarketOrderTestParameters(OptionStrategy strategy, IOrderProperties orderProperties = null)
    {
        _orderProperties = orderProperties;
        _name = $"{strategy.Name} ({strategy.CanonicalOption.Value})";

        var targetOption = strategy.CanonicalOption?.Canonical.ID.Symbol;

        _legs = new List<Leg>(strategy.UnderlyingLegs);
        foreach (var optionLeg in strategy.OptionLegs)
        {
            var option = Symbol.CreateOption(
                strategy.Underlying,
                targetOption,
                strategy.Underlying.ID.Market,
                strategy.Underlying.SecurityType.DefaultOptionStyle(),
                optionLeg.Right,
                optionLeg.Strike,
                optionLeg.Expiration);

            _legs.Add(new Leg { Symbol = option, Quantity = optionLeg.Quantity });
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ComboMarketOrderTestParameters"/> class
    /// that trades the same legs, with the same order properties, as the given combo limit order parameters.
    /// </summary>
    /// <param name="comboLimitOrderTestParameters">The combo limit order parameters to take the legs from.</param>
    public ComboMarketOrderTestParameters(ComboLimitOrderTestParameters comboLimitOrderTestParameters)
    {
        _name = comboLimitOrderTestParameters.ToString().Replace($"{OrderType.ComboLimit}: ", string.Empty);

        // The limit parameters keep their strategy private; a combo created for a quantity of 1 gives each leg with its ratio as the quantity.
        var comboLimitOrders = comboLimitOrderTestParameters.CreateLongOrder(1m);
        _orderProperties = comboLimitOrders.First().Properties;

        _legs = new List<Leg>(comboLimitOrders.Count);
        foreach (var comboLimitOrder in comboLimitOrders)
        {
            _legs.Add(new Leg { Symbol = comboLimitOrder.Symbol, Quantity = (int)comboLimitOrder.Quantity });
        }
    }

    /// <summary>
    /// Creates long combo orders (buy) for the specified quantity.
    /// </summary>
    /// <param name="quantity">The quantity of the combo order to create.</param>
    /// <returns>A collection of combo orders representing a long position.</returns>
    public IReadOnlyCollection<ComboOrder> CreateLongOrder(decimal quantity)
    {
        return CreateOrders(quantity);
    }

    /// <summary>
    /// Creates short combo orders (sell) for the specified quantity.
    /// </summary>
    /// <param name="quantity">The quantity of the combo order to create (will be negated internally).</param>
    /// <returns>A collection of combo orders representing a short position.</returns>
    public IReadOnlyCollection<ComboOrder> CreateShortOrder(decimal quantity)
    {
        return CreateOrders(decimal.Negate(Math.Abs(quantity)));
    }

    /// <summary>
    /// Returns a string representation of this instance for debugging and logging purposes.
    /// </summary>
    public override string ToString()
    {
        return $"{OrderType.ComboMarket}: {_name}";
    }

    /// <summary>
    /// Creates one combo market order per leg, all sharing one group order manager.
    /// </summary>
    /// <param name="quantity">The quantity of the whole combo; each leg trades it times its leg quantity.</param>
    /// <returns>A collection of <see cref="ComboOrder"/> instances for all legs.</returns>
    private IReadOnlyCollection<ComboOrder> CreateOrders(decimal quantity)
    {
        var groupOrderManager = new GroupOrderManager(_legs.Count, quantity);

        var orders = new List<ComboOrder>(_legs.Count);
        foreach (var leg in _legs)
        {
            orders.Add(new ComboMarketOrder(
                leg.Symbol,
                ((decimal)leg.Quantity).GetOrderLegGroupQuantity(groupOrderManager),
                DateTime.UtcNow,
                groupOrderManager,
                properties: _orderProperties)
            {
                Status = OrderStatus.New
            });
        }

        return orders;
    }
}
