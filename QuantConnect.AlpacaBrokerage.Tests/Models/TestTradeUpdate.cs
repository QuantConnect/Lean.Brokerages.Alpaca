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
