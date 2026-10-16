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
using System.Collections.Generic;
using System.Collections.Concurrent;
using OrderStatus = QuantConnect.Orders.OrderStatus;

namespace QuantConnect.Brokerages.Alpaca
{
    /// <summary>
    /// The open orders Alpaca had partially filled before Lean started, with the quantity it filled on each. Lean loads
    /// such an order with its status but without that quantity, which is already in the holdings it loads from the account,
    /// so the first fill update of the order reports only what filled after it.
    /// </summary>
    public class PartiallyFilledOpenOrders
    {
        /// <summary>
        /// The signed quantity filled before Lean started, by the Alpaca id of the order or of the leg of a multi-leg order.
        /// </summary>
        private readonly ConcurrentDictionary<Guid, decimal> _filledQuantityByBrokerageOrderId = [];

        /// <summary>
        /// Gets the Lean status of an open order and keeps the quantity Alpaca filled on it when that is part of the order.
        /// </summary>
        /// <param name="brokerageOrder">The Alpaca order, or one leg of a multi-leg order.</param>
        /// <returns><see cref="OrderStatus.PartiallyFilled"/> when Alpaca filled part of the order, otherwise <see cref="OrderStatus.Submitted"/>.</returns>
        public OrderStatus GetOrderStatus(IOrder brokerageOrder)
        {
            if (brokerageOrder.FilledQuantity > 0 && brokerageOrder.FilledQuantity != brokerageOrder.Quantity)
            {
                _filledQuantityByBrokerageOrderId[brokerageOrder.OrderId] =
                    brokerageOrder.OrderSide == OrderSide.Buy ? brokerageOrder.FilledQuantity : decimal.Negate(brokerageOrder.FilledQuantity);
                return OrderStatus.PartiallyFilled;
            }

            return OrderStatus.Submitted;
        }

        /// <summary>
        /// Gets the signed quantity Alpaca filled on the order before Lean started, zero when it filled none.
        /// </summary>
        /// <param name="brokerageOrderId">The Alpaca id of the order, or of the leg of a multi-leg order.</param>
        /// <returns>The signed quantity filled before Lean started, or zero.</returns>
        public decimal GetValueOrDefault(Guid brokerageOrderId)
        {
            return _filledQuantityByBrokerageOrderId.GetValueOrDefault(brokerageOrderId);
        }

        /// <summary>
        /// Moves the quantity to the order Alpaca created when it replaced the order, because the fill updates come under the new id.
        /// </summary>
        /// <param name="brokerageOrder">The replaced Alpaca order; a multi-leg order is replaced leg by leg, each under its own id.</param>
        public void Replace(IOrder brokerageOrder)
        {
            if (brokerageOrder.OrderClass == OrderClass.MultiLegOptions)
            {
                foreach (var leg in brokerageOrder.Legs)
                {
                    Replace(leg.OrderId, leg.ReplacedByOrderId.Value);
                }
            }
            else
            {
                Replace(brokerageOrder.OrderId, brokerageOrder.ReplacedByOrderId.Value);
            }
        }

        private void Replace(Guid brokerageOrderId, Guid replacedByBrokerageOrderId)
        {
            if (_filledQuantityByBrokerageOrderId.TryRemove(brokerageOrderId, out var filledQuantity))
            {
                _filledQuantityByBrokerageOrderId[replacedByBrokerageOrderId] = filledQuantity;
            }
        }
    }
}
