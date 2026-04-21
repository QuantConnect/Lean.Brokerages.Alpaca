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

using static Alpaca.Markets.OrderBaseExtensions;

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
        return orderRequest
            .WithDuration(order.TimeInForce.ConvertLeanTimeInForceToBrokerage(order.SecurityType, order.Type))
            .WithExtendedHours((order.Properties as AlpacaOrderProperties)?.OutsideRegularTradingHours ?? false);
    }

    /// <summary>
    /// Try to Convert Alpaca <see cref="AlpacaMarket.TimeInForce"/> to Lean <see cref="TimeInForce"/>
    /// </summary>
    /// <param name="orderProperties">The instance of Alpaca Order Properties.</param>
    /// <param name="timeInForce">The Alpaca Time In Force duration of order.</param>
    /// <returns>
    /// true - if it was converted successfully.
    /// false - if Alpaca Time In Force was not provided.
    /// </returns>
    public static bool TryGetLeanTimeInForceByAlpacaTimeInForce(this AlpacaOrderProperties orderProperties, AlpacaMarket.TimeInForce timeInForce)
    {
        switch (timeInForce)
        {
            case AlpacaMarket.TimeInForce.Day:
                orderProperties.TimeInForce = TimeInForce.Day;
                return true;
            case AlpacaMarket.TimeInForce.Gtc:
                orderProperties.TimeInForce = TimeInForce.GoodTilCanceled;
                return true;
            case AlpacaMarket.TimeInForce.Opg:
            case AlpacaMarket.TimeInForce.Cls:
                orderProperties.TimeInForce = TimeInForce.GoodTilCanceled;
                return true;
            default:
                return false;
        }
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
            case OrderType.MarketOnOpen:
            case OrderType.MarketOnClose:
                return AlpacaMarket.MarketOrder.Sell(brokerageSymbol, quantity);
            case OrderType.TrailingStop:
                var trailOffset = GetTrailOffsetValue(order as TrailingStopOrder);
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
            case OrderType.MarketOnOpen:
            case OrderType.MarketOnClose:
                return AlpacaMarket.MarketOrder.Buy(brokerageSymbol, quantity);
            case OrderType.TrailingStop:
                var trailOffset = GetTrailOffsetValue(order as TrailingStopOrder);
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
    /// <param name="securityType">The SecurityType of tradable security.</param>
    /// <param name="leanOrderType">The Lean order type.</param>
    /// <returns>Returns the corresponding AlpacaMarket.TimeInForce value.</returns>
    /// <exception cref="NotSupportedException">Thrown when the provided TimeInForce type is not supported.</exception>
    private static AlpacaMarket.TimeInForce ConvertLeanTimeInForceToBrokerage(this TimeInForce timeInForce, SecurityType securityType, OrderType leanOrderType)
    {
        if (securityType == SecurityType.Option && timeInForce is not DayTimeInForce)
        {
            Log.Error($"{nameof(AlpacaBrokerageExtensions)}.{nameof(ConvertLeanTimeInForceToBrokerage)}: Invalid TimeInForce '{timeInForce.GetType().Name}' for Option security type. Only 'DayTimeInForce' is supported for options.");
            return AlpacaMarket.TimeInForce.Day;
        }

        switch (leanOrderType)
        {
            case OrderType.MarketOnOpen:
                return AlpacaMarket.TimeInForce.Opg;
            case OrderType.MarketOnClose:
                return AlpacaMarket.TimeInForce.Cls;
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

    /// <summary>
    /// Gets the specific name of the underlying streaming client type,
    /// providing a more descriptive type name when wrapped.
    /// </summary>
    /// <param name="streamingClient">The streaming client instance.</param>
    /// <returns>The name of the underlying streaming client's type.</returns>
    public static string GetStreamingClientName(this AlpacaMarket.IStreamingClient streamingClient)
    {
        return streamingClient switch
        {
            AlpacaStreamingClientWrapper wrapper => wrapper.StreamingClient.GetType().Name,
            _ => streamingClient.GetType().Name
        };
    }

    /// <summary>
    /// Creates an Alpaca <see cref="AlpacaMarket.TrailOffset"/> from a Lean <see cref="TrailingStopOrder"/>.
    /// Converts Lean's fractional percent (e.g. 0.02 = 2%) to Alpaca's whole percent (e.g. 2 = 2%).
    /// </summary>
    public static AlpacaMarket.TrailOffset GetTrailOffsetValue(this TrailingStopOrder order)
    {
        return order.TrailingAsPercentage
            ? AlpacaMarket.TrailOffset.InPercent(order.TrailingAmount * 100m)
            : AlpacaMarket.TrailOffset.InDollars(order.TrailingAmount);
    }
}
