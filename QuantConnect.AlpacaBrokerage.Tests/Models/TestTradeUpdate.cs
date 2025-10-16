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

namespace QuantConnect.Brokerages.Alpaca.Tests.Models;

public record TestTradeUpdate(TradeEvent Event, Guid? ExecutionId, IOrder Order) : ITradeUpdate
{
    public decimal? Price { get; init; } = 1;
    public DateTime? TimestampUtc { get; init; }
    public decimal? PositionQuantity { get; init; }
    public long? PositionIntegerQuantity { get; init; }
    public decimal? TradeQuantity { get; init; }
    public long? TradeIntegerQuantity { get; init; }
}

public record TestOrder(Guid OrderId, decimal FilledQuantity = 0) : IOrder
{
    public string Symbol { get; init; } = "AAPL";
    public OrderSide OrderSide { get; init; }
    public AssetClass AssetClass { get; init; }
    public string ClientOrderId { get; init; }
    public DateTime? CreatedAtUtc { get; init; }
    public DateTime? UpdatedAtUtc { get; init; }
    public DateTime? SubmittedAtUtc { get; init; }
    public DateTime? FilledAtUtc { get; init; }
    public DateTime? ExpiredAtUtc { get; init; }
    public DateTime? CancelledAtUtc { get; init; }
    public DateTime? FailedAtUtc { get; init; }
    public DateTime? ReplacedAtUtc { get; init; }
    public Guid AssetId { get; init; }
    public decimal? Notional { get; init; }
    public decimal? Quantity { get; init; }
    public long IntegerQuantity { get; init; }
    public long IntegerFilledQuantity { get; init; }
    public OrderType OrderType { get; init; }
    public OrderClass OrderClass { get; init; }
    public TimeInForce TimeInForce { get; init; }
    public decimal? LimitPrice { get; init; }
    public decimal? StopPrice { get; init; }
    public decimal? TrailOffsetInDollars { get; init; }
    public decimal? TrailOffsetInPercent { get; init; }
    public decimal? HighWaterMark { get; init; }
    public decimal? AverageFillPrice { get; init; }
    public OrderStatus OrderStatus { get; init; }
    public Guid? ReplacedByOrderId { get; init; }
    public Guid? ReplacesOrderId { get; init; }
    public IReadOnlyList<IOrder> Legs { get; init; }
}
