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
    /// The quantity Alpaca last reported as filled on each order, signed, by the Alpaca id of the order or of the leg of a multi-leg
    /// order. Alpaca reports the cumulative quantity, Lean books the quantity filled since the last update. An order read back from
    /// the account starts with what Alpaca filled before Lean started, which Lean has no field for.
    /// </summary>
    public class CumulativeFilledQuantities
    {
        private readonly ConcurrentDictionary<Guid, decimal> _fillQuantityByBrokerageOrderId = [];

        /// <summary>
        /// Gets the Lean status of an open order read back from the account and keeps what Alpaca filled on it when that is part of the order.
        /// </summary>
        /// <param name="brokerageOrder">The Alpaca order, or one leg of a multi-leg order.</param>
        /// <returns><see cref="Orders.OrderStatus.PartiallyFilled"/> when Alpaca filled part of the order, otherwise <see cref="Orders.OrderStatus.Submitted"/>.</returns>
        public Orders.OrderStatus GetOrderStatus(IOrder brokerageOrder)
        {
            if (brokerageOrder.FilledQuantity > 0 && brokerageOrder.FilledQuantity != brokerageOrder.Quantity)
            {
                _fillQuantityByBrokerageOrderId[brokerageOrder.OrderId] = GetSignedFilledQuantity(brokerageOrder);
                return Orders.OrderStatus.PartiallyFilled;
            }

            return Orders.OrderStatus.Submitted;
        }

        /// <summary>
        /// Gets the signed quantity the order filled since its last update and keeps its new cumulative quantity, or drops it when the order is closed.
        /// </summary>
        /// <param name="brokerageOrder">The Alpaca order of the trade update, or one leg of a multi-leg order.</param>
        /// <param name="newLeanOrderStatus">The Lean status of the trade update.</param>
        /// <returns>The signed quantity filled since the last update; zero for a replayed update or a leg that did not trade.</returns>
        public decimal GetFillQuantity(IOrder brokerageOrder, Orders.OrderStatus newLeanOrderStatus)
        {
            _fillQuantityByBrokerageOrderId.TryGetValue(brokerageOrder.OrderId, out var previousFillQuantity);
            var fillQuantity = GetSignedFilledQuantity(brokerageOrder);
            if (newLeanOrderStatus.IsClosed())
            {
                _fillQuantityByBrokerageOrderId.TryRemove(brokerageOrder.OrderId, out _);
            }
            else
            {
                _fillQuantityByBrokerageOrderId[brokerageOrder.OrderId] = fillQuantity;
            }

            return fillQuantity - previousFillQuantity;
        }

        /// <summary>
        /// Moves the quantity to the order Alpaca created when it replaced the order, because the fill updates come under the new id.
        /// A multi-leg order is replaced leg by leg, each under its own id.
        /// </summary>
        /// <param name="brokerageOrder">The replaced Alpaca order.</param>
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
            if (_fillQuantityByBrokerageOrderId.TryRemove(brokerageOrderId, out var fillQuantity))
            {
                _fillQuantityByBrokerageOrderId[replacedByBrokerageOrderId] = fillQuantity;
            }
        }

        private static decimal GetSignedFilledQuantity(IOrder brokerageOrder)
        {
            return brokerageOrder.OrderSide == OrderSide.Buy ? brokerageOrder.FilledQuantity : decimal.Negate(brokerageOrder.FilledQuantity);
        }
    }
}
