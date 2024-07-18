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
using QuantConnect.Orders;
using QuantConnect.Logging;
using AlpacaMarket = Alpaca.Markets;
using QuantConnect.Orders.TimeInForces;

namespace QuantConnect.Brokerages.Alpaca;

public static class AlpacaBrokerageExtensions
{
    /// <summary>
    /// Creates an Alpaca sell order based on the provided Lean order type.
    /// </summary>
    /// <param name="order">The order object containing details for the trade.</param>
    /// <param name="brokerageSymbol">The symbol used by the brokerage for the asset being traded.</param>
    /// <param name="targetQuantity">The target order quantity which might not be the same quantity as the original order, due to cross zero holdings</param>
    /// <returns>Returns an OrderBase object representing the specific Alpaca sell order.</returns>
    /// <exception cref="NotSupportedException">Thrown when the order type is not supported.</exception>
    public static AlpacaMarket.OrderBase CreateAlpacaOrder(this Order order, decimal targetQuantity, ISymbolMapper symbolMapper, OrderType orderType)
    {
        var quantity = AlpacaMarket.OrderQuantity.Fractional(targetQuantity);
        var brokerageSymbol = symbolMapper.GetBrokerageSymbol(order.Symbol);
        var orderRequest = default(AlpacaMarket.OrderBase);
        if (order.Direction == OrderDirection.Buy)
        {
            orderRequest = order.CreateAlpacaBuyOrder(brokerageSymbol, quantity, orderType);
        }
        else if (order.Direction == OrderDirection.Sell)
        {
            orderRequest = order.CreateAlpacaSellOrder(brokerageSymbol, quantity, orderType);
        }
        else
        {
            throw new InvalidOperationException($"Can't create order for direction {order.Direction}");
        }
        var alpacaTimeInForce = order.TimeInForce.ConvertLeanTimeInForceToBrokerage(order.SecurityType);
        AlpacaMarket.OrderBaseExtensions.WithDuration(orderRequest, alpacaTimeInForce);
        return orderRequest;
    }

    /// <summary>
    /// Creates an Alpaca sell order based on the provided Lean order type.
    /// </summary>
    /// <param name="order">The order object containing details for the trade.</param>
    /// <param name="brokerageSymbol">The symbol used by the brokerage for the asset being traded.</param>
    /// <param name="targetQuantity">The target order quantity which might not be the same quantity as the original order, due to cross zero holdings</param>
    /// <returns>Returns an OrderBase object representing the specific Alpaca sell order.</returns>
    /// <exception cref="NotSupportedException">Thrown when the order type is not supported.</exception>
    private static AlpacaMarket.OrderBase CreateAlpacaSellOrder(this Order order, string brokerageSymbol, AlpacaMarket.OrderQuantity quantity, OrderType orderType)
    {
        switch (orderType)
        {
            case OrderType.Market:
                return AlpacaMarket.MarketOrder.Sell(brokerageSymbol, quantity);
            case OrderType.TrailingStop:
                var tso = (TrailingStopOrder)order;
                var trailOffset = tso.TrailingAsPercentage ? AlpacaMarket.TrailOffset.InPercent(tso.TrailingAmount) : AlpacaMarket.TrailOffset.InDollars(tso.TrailingAmount);
                return AlpacaMarket.TrailingStopOrder.Sell(brokerageSymbol, quantity, trailOffset);
            case OrderType.Limit:
                decimal limitPrice;
                if (order is StopLimitOrder stopLimitOrder)
                {
                    // for cross zero, second part of StopLimit is converted to limit, see 'CrossZeroSecondOrderRequest'
                    limitPrice = stopLimitOrder.LimitPrice;
                }
                else
                {
                    limitPrice = ((LimitOrder)order).LimitPrice;
                }
                return AlpacaMarket.LimitOrder.Sell(brokerageSymbol, quantity, limitPrice);
            case OrderType.StopMarket:
                return AlpacaMarket.StopOrder.Sell(brokerageSymbol, quantity, ((StopMarketOrder)order).StopPrice);
            case OrderType.StopLimit:
                return AlpacaMarket.StopLimitOrder.Sell(brokerageSymbol, quantity, ((StopLimitOrder)order).StopPrice, ((StopLimitOrder)order).LimitPrice);
            default:
                throw new NotSupportedException($"{nameof(AlpacaBrokerageExtensions)}.{nameof(CreateAlpacaSellOrder)}: The order type '{order.GetType().Name}' is not supported for Alpaca sell orders.");
        };
    }

    /// <summary>
    /// Creates an Alpaca buy order based on the provided Lean order type.
    /// </summary>
    /// <param name="order">The order object containing details for the trade.</param>
    /// <param name="brokerageSymbol">The symbol used by the brokerage for the asset being traded.</param>
    /// <param name="targetQuantity">The target order quantity which might not be the same quantity as the original order, due to cross zero holdings</param>
    /// <returns>Returns an OrderBase object representing the specific Alpaca buy order.</returns>
    /// <exception cref="NotSupportedException">Thrown when the order type is not supported.</exception>
    private static AlpacaMarket.OrderBase CreateAlpacaBuyOrder(this Order order, string brokerageSymbol, AlpacaMarket.OrderQuantity quantity, OrderType orderType)
    {
        switch (orderType)
        {
            case OrderType.Market:
                return AlpacaMarket.MarketOrder.Buy(brokerageSymbol, quantity);
            case OrderType.TrailingStop:
                var tso = (TrailingStopOrder)order;
                var trailOffset = tso.TrailingAsPercentage ? AlpacaMarket.TrailOffset.InPercent(tso.TrailingAmount) : AlpacaMarket.TrailOffset.InDollars(tso.TrailingAmount);
                return AlpacaMarket.TrailingStopOrder.Buy(brokerageSymbol, quantity, trailOffset);
            case OrderType.Limit:
                decimal limitPrice;
                if (order is StopLimitOrder stopLimitOrder)
                {
                    // for cross zero, second part of StopLimit is converted to limit, see 'CrossZeroSecondOrderRequest'
                    limitPrice = stopLimitOrder.LimitPrice;
                }
                else
                {
                    limitPrice = ((LimitOrder)order).LimitPrice;
                }
                return AlpacaMarket.LimitOrder.Buy(brokerageSymbol, quantity, limitPrice);
            case OrderType.StopMarket:
                return AlpacaMarket.StopOrder.Buy(brokerageSymbol, quantity, ((StopMarketOrder)order).StopPrice);
            case OrderType.StopLimit:
                return AlpacaMarket.StopLimitOrder.Buy(brokerageSymbol, quantity, ((StopLimitOrder)order).StopPrice, ((StopLimitOrder)order).LimitPrice);
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
    private static AlpacaMarket.TimeInForce ConvertLeanTimeInForceToBrokerage(this TimeInForce timeInForce, SecurityType securityType)
    {
        if (securityType == SecurityType.Option && timeInForce is not DayTimeInForce)
        {
            Log.Error($"{nameof(AlpacaBrokerageExtensions)}.{nameof(ConvertLeanTimeInForceToBrokerage)}: Invalid TimeInForce '{timeInForce.GetType().Name}' for Option security type. Only 'DayTimeInForce' is supported for options.");
            return AlpacaMarket.TimeInForce.Day;
        }

        return timeInForce switch
        {
            DayTimeInForce => AlpacaMarket.TimeInForce.Day,
            GoodTilCanceledTimeInForce => AlpacaMarket.TimeInForce.Gtc,
            _ => throw new NotSupportedException($"{nameof(AlpacaBrokerageExtensions)}.{nameof(ConvertLeanTimeInForceToBrokerage)}:The provided TimeInForce type '{timeInForce.GetType().Name}' is not supported.")
        };
    }

    /// <summary>
    /// Converts a Lean resolution to an Alpaca BarTimeFrame.
    /// </summary>
    /// <param name="leanResolution">The resolution of data requested.</param>
    /// <returns>The corresponding Alpaca BarTimeFrame.</returns>
    /// <exception cref="NotImplementedException">
    /// Thrown when an unsupported resolution is provided.
    /// </exception>
    public static AlpacaMarket.BarTimeFrame ConvertLeanResolutionToAlpacaBarTimeFrame(this Resolution leanResolution) => leanResolution switch
    {
        Resolution.Minute => AlpacaMarket.BarTimeFrame.Minute,
        Resolution.Hour => AlpacaMarket.BarTimeFrame.Hour,
        Resolution.Daily => AlpacaMarket.BarTimeFrame.Day,
        _ => throw new NotImplementedException($"{nameof(AlpacaBrokerageExtensions)}.{nameof(ConvertLeanResolutionToAlpacaBarTimeFrame)}: " +
            $"The resolution '{leanResolution}' is not supported. Please use Minute, Hour, or Daily resolution.")
    };
}
