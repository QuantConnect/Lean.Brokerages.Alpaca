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
using Alpaca.Markets;
using QuantConnect.Orders;
using System.Collections.Concurrent;

namespace QuantConnect.Brokerages.Alpaca
{
    /// <summary>
    /// What Alpaca last reported as filled on each order, by the Alpaca id of the order or of its leg: the signed quantity
    /// and the average price. Alpaca reports totals, Lean books only what filled since the last update. An order read back
    /// at startup starts with what filled before Lean ran, which Lean has no field for.
    /// </summary>
    internal class CumulativeFilledQuantities
    {
        private readonly ConcurrentDictionary<Guid, (decimal Quantity, decimal AveragePrice)> _filledByBrokerageOrderId = [];

        /// <summary>
        /// Gets the Lean status of an open order read back at startup. A partly filled order also gets its filled quantity and price kept.
        /// </summary>
        /// <param name="brokerageOrder">The Alpaca order, or a leg of a multi-leg order.</param>
        /// <returns>PartiallyFilled when part of the order filled, otherwise Submitted.</returns>
        public Orders.OrderStatus GetOrderStatus(IOrder brokerageOrder)
        {
            if (brokerageOrder.FilledQuantity > 0 && brokerageOrder.FilledQuantity != brokerageOrder.Quantity)
            {
                var filled = GetFilled(brokerageOrder);
                _filledByBrokerageOrderId.AddOrUpdate(brokerageOrder.OrderId, filled, (_, known) => Math.Abs(known.Quantity) > Math.Abs(filled.Quantity) ? known : filled);
                return Orders.OrderStatus.PartiallyFilled;
            }

            return Orders.OrderStatus.Submitted;
        }

        /// <summary>
        /// Gets the signed quantity filled since the last update and its price, then keeps the new totals, or drops them when the fill closes the order.
        /// </summary>
        /// <param name="brokerageOrder">The Alpaca order of the trade update, or a leg of a multi-leg order.</param>
        /// <param name="newLeanOrderStatus">The Lean status of the trade update.</param>
        /// <returns>The quantity, zero when nothing new filled or the report is older than the stored one, and its price.</returns>
        public (decimal Quantity, decimal Price) GetFill(IOrder brokerageOrder, Orders.OrderStatus newLeanOrderStatus)
        {
            _filledByBrokerageOrderId.TryGetValue(brokerageOrder.OrderId, out var previous);
            var (filledQuantity, averageFillPrice) = GetFilled(brokerageOrder);
            if (Math.Abs(filledQuantity) < Math.Abs(previous.Quantity))
            {
                // an older report than the stored one, the stream fell behind the account read
                return (0, averageFillPrice);
            }

            if (newLeanOrderStatus.IsClosed())
            {
                _filledByBrokerageOrderId.TryRemove(brokerageOrder.OrderId, out _);
            }
            else
            {
                _filledByBrokerageOrderId[brokerageOrder.OrderId] = (filledQuantity, averageFillPrice);
            }

            var fillQuantity = filledQuantity - previous.Quantity;
            if (fillQuantity == 0)
            {
                return (0, averageFillPrice);
            }

            // the average covers everything filled so far, taking out the value filled before leaves the price of the new quantity
            var fillPrice = (filledQuantity * averageFillPrice - previous.Quantity * previous.AveragePrice) / fillQuantity;
            return (fillQuantity, fillPrice);
        }

        /// <summary>
        /// Moves the values to the order that replaced this one, since its fill updates come under the new id. A multi-leg order is replaced leg by leg.
        /// </summary>
        /// <param name="brokerageOrder">The replaced Alpaca order.</param>
        public void Replace(IOrder brokerageOrder)
        {
            if (brokerageOrder.OrderClass == OrderClass.MultiLegOptions)
            {
                foreach (var leg in brokerageOrder.Legs)
                {
                    if (leg.ReplacedByOrderId.HasValue)
                    {
                        Replace(leg.OrderId, leg.ReplacedByOrderId.Value);
                    }
                }
            }
            else
            {
                Replace(brokerageOrder.OrderId, brokerageOrder.ReplacedByOrderId.Value);
            }
        }

        private void Replace(Guid brokerageOrderId, Guid replacedByBrokerageOrderId)
        {
            if (_filledByBrokerageOrderId.TryRemove(brokerageOrderId, out var filled))
            {
                _filledByBrokerageOrderId[replacedByBrokerageOrderId] = filled;
            }
        }

        private static (decimal Quantity, decimal AveragePrice) GetFilled(IOrder brokerageOrder)
        {
            var quantity = brokerageOrder.OrderSide == OrderSide.Buy ? brokerageOrder.FilledQuantity : decimal.Negate(brokerageOrder.FilledQuantity);
            return (quantity, brokerageOrder.AverageFillPrice ?? 0m);
        }
    }
}
