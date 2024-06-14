﻿/*
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
using Alpaca.Markets;
using QuantConnect.Orders;
using AlpacaMarket = Alpaca.Markets;
using LeanOrders = QuantConnect.Orders;
using QuantConnect.Orders.TimeInForces;

namespace QuantConnect.Brokerages.Alpaca;

public static class AlpacaBrokerageExtensions
{
    /// <summary>
    /// Creates an Alpaca sell order based on the provided Lean order type.
    /// </summary>
    /// <param name="order">The order object containing details for the trade.</param>
    /// <param name="brokerageSymbol">The symbol used by the brokerage for the asset being traded.</param>
    /// <returns>Returns an OrderBase object representing the specific Alpaca sell order.</returns>
    /// <exception cref="NotSupportedException">Thrown when the order type is not supported.</exception>
    public static OrderBase CreateAlpacaSellOrder(this Order order, string brokerageSymbol)
    {
        var quantity = Convert.ToInt64(order.AbsoluteQuantity);
        switch (order)
        {
            case LeanOrders.MarketOrder:
                return AlpacaMarket.MarketOrder.Sell(brokerageSymbol, quantity);
            case LeanOrders.TrailingStopOrder tso:
                var trailOffset = tso.TrailingAsPercentage ? TrailOffset.InPercent(tso.TrailingAmount) : TrailOffset.InDollars(tso.TrailingAmount);
                return AlpacaMarket.TrailingStopOrder.Sell(brokerageSymbol, quantity, trailOffset);
            case LeanOrders.LimitOrder lo:
                return AlpacaMarket.LimitOrder.Sell(brokerageSymbol, quantity, lo.LimitPrice);
            case StopMarketOrder smo:
                return StopOrder.Sell(brokerageSymbol, quantity, smo.StopPrice);
            case LeanOrders.StopLimitOrder slo:
                return AlpacaMarket.StopLimitOrder.Sell(brokerageSymbol, quantity, slo.StopPrice, slo.LimitPrice);
            default:
                throw new NotSupportedException($"{nameof(AlpacaBrokerageExtensions)}.{nameof(CreateAlpacaSellOrder)}: The order type '{order.GetType().Name}' is not supported for Alpaca sell orders.");
        };
    }

    /// <summary>
    /// Creates an Alpaca buy order based on the provided Lean order type.
    /// </summary>
    /// <param name="order">The order object containing details for the trade.</param>
    /// <param name="brokerageSymbol">The symbol used by the brokerage for the asset being traded.</param>
    /// <returns>Returns an OrderBase object representing the specific Alpaca buy order.</returns>
    /// <exception cref="NotSupportedException">Thrown when the order type is not supported.</exception>
    public static OrderBase CreateAlpacaBuyOrder(this Order order, string brokerageSymbol)
    {
        var quantity = Convert.ToInt64(order.AbsoluteQuantity);
        switch (order)
        {
            case LeanOrders.MarketOrder:
                return AlpacaMarket.MarketOrder.Buy(brokerageSymbol, quantity);
            case LeanOrders.TrailingStopOrder tso:
                var trailOffset = tso.TrailingAsPercentage ? TrailOffset.InPercent(tso.TrailingAmount) : TrailOffset.InDollars(tso.TrailingAmount);
                return AlpacaMarket.TrailingStopOrder.Buy(brokerageSymbol, quantity, trailOffset);
            case LeanOrders.LimitOrder lo:
                return AlpacaMarket.LimitOrder.Buy(brokerageSymbol, quantity, lo.LimitPrice);
            case StopMarketOrder smo:
                return StopOrder.Buy(brokerageSymbol, quantity, smo.StopPrice);
            case LeanOrders.StopLimitOrder slo:
                return AlpacaMarket.StopLimitOrder.Buy(brokerageSymbol, quantity, slo.StopPrice, slo.LimitPrice);
            default:
                throw new NotSupportedException($"{nameof(AlpacaBrokerageExtensions)}.{nameof(CreateAlpacaBuyOrder)}: The order type '{order.GetType().Name}' is not supported for Alpaca buy orders.");
        };
    }

    /// <summary>
    /// Converts Lean TimeInForce to Alpaca brokerage TimeInForce.
    /// </summary>
    /// <param name="timeInForce">The Lean TimeInForce object to be converted.</param>
    /// <returns>Returns the corresponding AlpacaMarket.TimeInForce value.</returns>
    /// <exception cref="NotSupportedException">Thrown when the provided TimeInForce type is not supported.</exception>
    public static AlpacaMarket.TimeInForce ConvertLeanTimeInForceToBrokerage(this LeanOrders.TimeInForce timeInForce) => timeInForce switch
    {
        DayTimeInForce => AlpacaMarket.TimeInForce.Day,
        GoodTilCanceledTimeInForce => AlpacaMarket.TimeInForce.Gtc,
        _ => throw new NotSupportedException($"{nameof(AlpacaBrokerageExtensions)}.{nameof(ConvertLeanTimeInForceToBrokerage)}:The provided TimeInForce type '{timeInForce.GetType().Name}' is not supported.")
    };
}