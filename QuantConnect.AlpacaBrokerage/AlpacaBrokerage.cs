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
using Alpaca.Markets;
using System.Threading;
using QuantConnect.Data;
using QuantConnect.Util;
using QuantConnect.Orders;
using QuantConnect.Packets;
using QuantConnect.Logging;
using QuantConnect.Interfaces;
using QuantConnect.Securities;
using QuantConnect.Orders.Fees;
using System.Collections.Generic;
using AlpacaMarket = Alpaca.Markets;
using System.Collections.Concurrent;
using LeanOrders = QuantConnect.Orders;

namespace QuantConnect.Brokerages.Alpaca
{
    [BrokerageFactory(typeof(AlpacaBrokerageFactory))]
    public class AlpacaBrokerage : Brokerage, IDataQueueHandler, IDataQueueUniverseProvider
    {
        /// <inheritdoc cref="IDataAggregator"/>
        private readonly IDataAggregator _aggregator;

        /// <inheritdoc cref="IOrderProvider"/>
        private IOrderProvider _orderProvider;

        private readonly EventBasedDataQueueHandlerSubscriptionManager _subscriptionManager;

        /// <inheritdoc cref="AlpacaBrokerageSymbolMapper"/>
        private AlpacaBrokerageSymbolMapper _symbolMapper;

        /// <inheritdoc cref="IAlpacaTradingClient"/>
        private IAlpacaTradingClient AlpacaTradingClient { get; }

        /// <inheritdoc cref="IAlpacaStreamingClient"/>
        private IAlpacaStreamingClient AlpacaStreamingClient { get; }

        private object _lockObject = new();

        private readonly ConcurrentDictionary<string, AutoResetEvent> _resetEventByBrokerageOrderID = new();

        /// <summary>
        /// Indicates whether the application is subscribed to stream order updates.
        /// </summary>
        private bool _isAuthorizedOnStreamOrderUpdate;

        /// <summary>
        /// Returns true if we're currently connected to the broker
        /// </summary>
        public override bool IsConnected { get => _isAuthorizedOnStreamOrderUpdate; }

        /// <summary>
        /// Initializes a new instance of the <see cref="AlpacaBrokerage"/> class.
        /// </summary>
        /// <param name="apiKey">The API key for authentication with Alpaca.</param>
        /// <param name="apiKeySecret">The secret key for authentication with Alpaca.</param>
        /// <param name="isPaperTrading">Indicates whether the brokerage should use the paper trading environment.</param>
        /// <remarks>
        /// This constructor initializes a new instance of the <see cref="AlpacaBrokerage"/> class with the specified API key,
        /// API secret key, and a flag indicating whether to use paper trading. It also retrieves an instance of <see cref="IDataAggregator"/>
        /// from the <see cref="Composer"/>. This constructor is required for brokerages implementing <see cref="IDataQueueHandler"/>.
        /// </remarks>
        public AlpacaBrokerage(string apiKey, string apiKeySecret, bool isPaperTrading, IAlgorithm algorithm)
            : this(apiKey, apiKeySecret, isPaperTrading, algorithm?.Portfolio?.Transactions, Composer.Instance.GetPart<IDataAggregator>())
        { }

        /// <summary>
        /// Initializes a new instance of the <see cref="AlpacaBrokerage"/> class.
        /// </summary>
        /// <param name="apiKey">The API key for authentication with Alpaca.</param>
        /// <param name="apiKeySecret">The secret key for authentication with Alpaca.</param>
        /// <param name="isPaperTrading">Indicates whether the brokerage should use the paper trading environment.</param>
        /// <param name="aggregator">The data aggregator used for handling data streams.</param>
        /// <remarks>
        /// This constructor initializes a new instance of the <see cref="AlpacaBrokerage"/> class with the specified API key,
        /// API secret key, a flag indicating whether to use paper trading, and an instance of <see cref="IDataAggregator"/>.
        /// </remarks>
        public AlpacaBrokerage(string apiKey, string apiKeySecret, bool isPaperTrading, IOrderProvider orderProvider, IDataAggregator aggregator) : base("AlpacaBrokerage")
        {
            var secretKey = new SecretKey(apiKey, apiKeySecret);

            if (isPaperTrading)
            {
                AlpacaTradingClient = Environments.Paper.GetAlpacaTradingClient(secretKey);
                AlpacaStreamingClient = Environments.Paper.GetAlpacaStreamingClient(secretKey);
            }
            else
            {
                AlpacaTradingClient = Environments.Live.GetAlpacaTradingClient(secretKey);
                AlpacaStreamingClient = Environments.Live.GetAlpacaStreamingClient(secretKey);
            }

            AlpacaStreamingClient.OnTradeUpdate += HandleTradeUpdate;

            _symbolMapper = new AlpacaBrokerageSymbolMapper();
            _orderProvider = orderProvider;

            _aggregator = aggregator;
            _subscriptionManager = new EventBasedDataQueueHandlerSubscriptionManager();
            _subscriptionManager.SubscribeImpl += (s, t) => Subscribe(s);
            _subscriptionManager.UnsubscribeImpl += (s, t) => Unsubscribe(s);

            // Useful for some brokerages:

            // Brokerage helper class to lock websocket message stream while executing an action, for example placing an order
            // avoid race condition with placing an order and getting filled events before finished placing
            // _messageHandler = new BrokerageConcurrentMessageHandler<>();

            // Rate gate limiter useful for API/WS calls
            // _connectionRateLimiter = new RateGate();
        }

        #region IDataQueueHandler

        /// <summary>
        /// Subscribe to the specified configuration
        /// </summary>
        /// <param name="dataConfig">defines the parameters to subscribe to a data feed</param>
        /// <param name="newDataAvailableHandler">handler to be fired on new data available</param>
        /// <returns>The new enumerator for this subscription request</returns>
        public IEnumerator<BaseData> Subscribe(SubscriptionDataConfig dataConfig, EventHandler newDataAvailableHandler)
        {
            if (!CanSubscribe(dataConfig.Symbol))
            {
                return null;
            }

            var enumerator = _aggregator.Add(dataConfig, newDataAvailableHandler);
            _subscriptionManager.Subscribe(dataConfig);

            return enumerator;
        }

        /// <summary>
        /// Removes the specified configuration
        /// </summary>
        /// <param name="dataConfig">Subscription config to be removed</param>
        public void Unsubscribe(SubscriptionDataConfig dataConfig)
        {
            _subscriptionManager.Unsubscribe(dataConfig);
            _aggregator.Remove(dataConfig);
        }

        /// <summary>
        /// Sets the job we're subscribing for
        /// </summary>
        /// <param name="job">Job we're subscribing for</param>
        public void SetJob(LiveNodePacket job)
        {
            throw new NotImplementedException();
        }

        #endregion

        #region Brokerage

        /// <summary>
        /// Gets all open orders on the account.
        /// NOTE: The order objects returned do not have QC order IDs.
        /// </summary>
        /// <returns>The open orders returned from IB</returns>
        public override List<Order> GetOpenOrders()
        {
            var orders = AlpacaTradingClient.ListOrdersAsync(new ListOrdersRequest() { OrderStatusFilter = OrderStatusFilter.Open }).SynchronouslyAwaitTaskResult();

            var leanOrders = new List<Order>();
            foreach (var brokerageOrder in orders)
            {
                var leanSymbol = _symbolMapper.GetLeanSymbol(brokerageOrder.AssetClass, brokerageOrder.Symbol);
                var quantity = (brokerageOrder.OrderSide == OrderSide.Buy ? brokerageOrder.Quantity : decimal.Negate(brokerageOrder.Quantity.Value)).Value;
                var leanOrder = default(Order);
                switch (brokerageOrder.OrderType)
                {
                    case AlpacaMarket.OrderType.Market:
                        leanOrder = new LeanOrders.MarketOrder(leanSymbol, quantity, brokerageOrder.SubmittedAtUtc.Value);
                        break;
                    case AlpacaMarket.OrderType.Limit:
                        leanOrder = new LeanOrders.LimitOrder(leanSymbol, quantity, brokerageOrder.LimitPrice.Value, brokerageOrder.SubmittedAtUtc.Value);
                        break;
                    case AlpacaMarket.OrderType.Stop:
                        leanOrder = new StopMarketOrder(leanSymbol, quantity, brokerageOrder.StopPrice.Value, brokerageOrder.SubmittedAtUtc.Value);
                        break;
                    case AlpacaMarket.OrderType.StopLimit:
                        leanOrder = new LeanOrders.StopLimitOrder(leanSymbol, quantity, brokerageOrder.StopPrice.Value, brokerageOrder.LimitPrice.Value, brokerageOrder.SubmittedAtUtc.Value);
                        break;
                    case AlpacaMarket.OrderType.TrailingStop:
                        var trailingAsPercent = brokerageOrder.TrailOffsetInPercent.HasValue ? true : false;
                        var trailingAmount = brokerageOrder.TrailOffsetInPercent.HasValue ? brokerageOrder.TrailOffsetInPercent.Value / 100m : brokerageOrder.TrailOffsetInDollars.Value;
                        leanOrder = new LeanOrders.TrailingStopOrder(leanSymbol, quantity, brokerageOrder.StopPrice.Value, trailingAmount, trailingAsPercent, brokerageOrder.SubmittedAtUtc.Value);
                        break;
                    default:
                        throw new NotSupportedException($"{nameof(AlpacaBrokerage)}.{nameof(GetOpenOrders)}: Order type '{brokerageOrder.OrderType}' is not supported.");
                }

                leanOrder.Status = LeanOrders.OrderStatus.Submitted;

                if (brokerageOrder.FilledQuantity > 0 && brokerageOrder.FilledQuantity != brokerageOrder.Quantity)
                {
                    leanOrder.Status = LeanOrders.OrderStatus.PartiallyFilled;
                }

                leanOrder.BrokerId.Add(brokerageOrder.OrderId.ToString());
                leanOrders.Add(leanOrder);
            }

            return leanOrders;
        }

        /// <summary>
        /// Gets all holdings for the account
        /// </summary>
        /// <returns>The current holdings from the account</returns>
        public override List<Holding> GetAccountHoldings()
        {
            var positions = AlpacaTradingClient.ListPositionsAsync().SynchronouslyAwaitTaskResult();

            var holdings = new List<Holding>();
            foreach (var position in positions)
            {
                holdings.Add(new Holding()
                {
                    AveragePrice = position.AverageEntryPrice,
                    CurrencySymbol = Currencies.USD,
                    MarketValue = position.MarketValue ?? 0m,
                    MarketPrice = position.AssetCurrentPrice ?? 0m,
                    Quantity = position.Quantity,
                    Symbol = _symbolMapper.GetLeanSymbol(position.AssetClass, position.Symbol),
                    UnrealizedPnL = position.UnrealizedProfitLoss ?? 0m,
                    UnrealizedPnLPercent = position.UnrealizedProfitLossPercent ?? 0m,
                });
            }
            return holdings;
        }

        /// <summary>
        /// Gets the current cash balance for each currency held in the brokerage account
        /// </summary>
        /// <returns>The current cash balance for each currency available for trading</returns>
        public override List<CashAmount> GetCashBalance()
        {
            var accounts = AlpacaTradingClient.GetAccountAsync().SynchronouslyAwaitTaskResult();
            return new List<CashAmount>() { new CashAmount(accounts.TradableCash, accounts.Currency) };
        }

        /// <summary>
        /// Places a new order and assigns a new broker ID to the order
        /// </summary>
        /// <param name="order">The order to be placed</param>
        /// <returns>True if the request for a new order has been placed, false otherwise</returns>
        public override bool PlaceOrder(Order order)
        {
            if (!CanSubscribe(order.Symbol))
            {
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, -1, $"Symbol is not supported {order.Symbol}"));
                return false;
            }

            var brokerageSymbol = _symbolMapper.GetBrokerageSymbol(order.Symbol);

            var orderRequest = default(OrderBase);
            if (order.Direction == OrderDirection.Buy)
            {
                orderRequest = order.CreateAlpacaBuyOrder(brokerageSymbol);
            }
            else if (order.Direction == OrderDirection.Sell)
            {
                orderRequest = order.CreateAlpacaSellOrder(brokerageSymbol);
            }

            orderRequest.WithDuration(order.TimeInForce.ConvertLeanTimeInForceToBrokerage(order.SecurityType));

            var placeOrderResetEvent = new AutoResetEvent(false);
            try
            {
                lock (_lockObject)
                {
                    var response = AlpacaTradingClient.PostOrderAsync(orderRequest).SynchronouslyAwaitTaskResult();
                    order.BrokerId.Add(response.OrderId.ToString());
                    _resetEventByBrokerageOrderID[response.OrderId.ToString()] = placeOrderResetEvent;
                }

                if (placeOrderResetEvent.WaitOne(TimeSpan.FromSeconds(10)))
                {
                    OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero, $"{nameof(AlpacaBrokerage)} Order Event")
                    {
                        Status = LeanOrders.OrderStatus.Submitted
                    });
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero, $"{nameof(AlpacaBrokerage)}.{nameof(PlaceOrder)} Order Event")
                { Status = LeanOrders.OrderStatus.Invalid, Message = ex.Message });
                return false;
            }
        }

        private void HandleTradeUpdate(ITradeUpdate obj)
        {
            Log.Debug($"{nameof(AlpacaBrokerage)}.{nameof(HandleTradeUpdate)}: {obj}");

            var leanOrderStatus = default(LeanOrders.OrderStatus);
            switch (obj.Event)
            {
                case TradeEvent.PendingNew:
                    return;
                case TradeEvent.New:
                    if (_resetEventByBrokerageOrderID.TryRemove(obj.Order.OrderId.ToString(), out var resetEvent))
                    {
                        resetEvent.Set();
                    }
                    return;
                case TradeEvent.Rejected:
                    if (_resetEventByBrokerageOrderID.TryRemove(obj.Order.OrderId.ToString(), out resetEvent))
                    {
                        resetEvent.Set();
                    }
                    break;
                case TradeEvent.Canceled:
                    if (_resetEventByBrokerageOrderID.TryRemove(obj.Order.OrderId.ToString(), out resetEvent))
                    {
                        resetEvent.Set();
                    }
                    return;
                case TradeEvent.Fill:
                    leanOrderStatus = LeanOrders.OrderStatus.Filled;
                    break;
                case TradeEvent.PartialFill:
                    leanOrderStatus = LeanOrders.OrderStatus.PartiallyFilled;
                    break;
                default:
                    return;
            }

            var leanOrder = _orderProvider.GetOrdersByBrokerageId(obj.Order.OrderId.ToString())?.SingleOrDefault();

            if (leanOrder == null)
            {
                Log.Error($"{nameof(AlpacaBrokerage)}.{nameof(HandleTradeUpdate)}. order id not found: {obj.Order.OrderId}");
                return;
            }

            var leanSymbol = _symbolMapper.GetLeanSymbol(obj.Order.AssetClass, obj.Order.Symbol);

            var orderEvent = new OrderEvent(leanOrder.Id,
                leanSymbol,
                obj.TimestampUtc.HasValue ? obj.TimestampUtc.Value : obj.Order.SubmittedAtUtc.Value,
                leanOrder.Status,
                obj.Order.OrderSide == OrderSide.Buy ? OrderDirection.Buy : OrderDirection.Sell,
                obj.Price ?? 0m,
                obj.Order.OrderSide == OrderSide.Buy ? obj.Order.FilledQuantity : Decimal.Negate(obj.Order.FilledQuantity),
                new OrderFee(new CashAmount(1, Currencies.USD)))
            { Status = leanOrderStatus };

            OnOrderEvent(orderEvent);
        }

        /// <summary>
        /// Updates the order with the same id
        /// </summary>
        /// <param name="order">The new order information</param>
        /// <returns>True if the request was made for the order to be updated, false otherwise</returns>
        public override bool UpdateOrder(Order order)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Cancels the order with the specified ID
        /// </summary>
        /// <param name="order">The order to cancel</param>
        /// <returns>True if the request was made for the order to be canceled, false otherwise</returns>
        public override bool CancelOrder(Order order)
        {
            try
            {
                var brokerageOrderId = order.BrokerId.Last();

                var cancelOrderResetEvent = new AutoResetEvent(false);
                lock (_lockObject)
                {
                    var response = AlpacaTradingClient.CancelOrderAsync(new Guid(brokerageOrderId)).SynchronouslyAwaitTaskResult();
                    _resetEventByBrokerageOrderID[brokerageOrderId] = cancelOrderResetEvent;
                }

                if (cancelOrderResetEvent.WaitOne(TimeSpan.FromSeconds(10)))
                {
                    OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero, $"{nameof(AlpacaBrokerage)} Order Event")
                    {
                        Status = LeanOrders.OrderStatus.Canceled
                    });
                    return true;
                }

                return false;

            }
            catch (Exception ex)
            {
                OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero, $"{nameof(AlpacaBrokerage)}.{nameof(CancelOrder)} Order Event")
                { Status = LeanOrders.OrderStatus.Invalid, Message = ex.Message });
                return false;
            }
        }

        /// <summary>
        /// Connects the client to the broker's remote servers
        /// </summary>
        public override void Connect()
        {
            var authorizedStatus = AlpacaStreamingClient.ConnectAndAuthenticateAsync().SynchronouslyAwaitTaskResult();
            _isAuthorizedOnStreamOrderUpdate = authorizedStatus == AuthStatus.Authorized;
        }

        /// <summary>
        /// Disconnects the client from the broker's remote servers
        /// </summary>
        public override void Disconnect()
        {
            AlpacaStreamingClient.DisconnectAsync().SynchronouslyAwaitTask();
        }

        public override void Dispose()
        {
            AlpacaStreamingClient.DisposeSafely();
            AlpacaTradingClient.DisposeSafely();
        }

        #endregion

        #region IDataQueueUniverseProvider

        /// <summary>
        /// Method returns a collection of Symbols that are available at the data source.
        /// </summary>
        /// <param name="symbol">Symbol to lookup</param>
        /// <param name="includeExpired">Include expired contracts</param>
        /// <param name="securityCurrency">Expected security currency(if any)</param>
        /// <returns>Enumerable of Symbols, that are associated with the provided Symbol</returns>
        public IEnumerable<Symbol> LookupSymbols(Symbol symbol, bool includeExpired, string securityCurrency = null)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Returns whether selection can take place or not.
        /// </summary>
        /// <remarks>This is useful to avoid a selection taking place during invalid times, for example IB reset times or when not connected,
        /// because if allowed selection would fail since IB isn't running and would kill the algorithm</remarks>
        /// <returns>True if selection can take place</returns>
        public bool CanPerformSelection()
        {
            throw new NotImplementedException();
        }

        #endregion

        private bool CanSubscribe(Symbol symbol)
        {
            if (symbol.Value.IndexOfInvariant("universe", true) != -1 || symbol.IsCanonical())
            {
                return false;
            }

            return _symbolMapper.SupportedSecurityType.Contains(symbol.SecurityType);
        }

        /// <summary>
        /// Adds the specified symbols to the subscription
        /// </summary>
        /// <param name="symbols">The symbols to be added keyed by SecurityType</param>
        private bool Subscribe(IEnumerable<Symbol> symbols)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Removes the specified symbols to the subscription
        /// </summary>
        /// <param name="symbols">The symbols to be removed keyed by SecurityType</param>
        private bool Unsubscribe(IEnumerable<Symbol> symbols)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Gets the history for the requested symbols
        /// <see cref="IBrokerage.GetHistory(Data.HistoryRequest)"/>
        /// </summary>
        /// <param name="request">The historical data request</param>
        /// <returns>An enumerable of bars covering the span specified in the request</returns>
        public override IEnumerable<BaseData> GetHistory(Data.HistoryRequest request)
        {
            if (!CanSubscribe(request.Symbol))
            {
                return null; // Should consistently return null instead of an empty enumerable
            }

            throw new NotImplementedException();
        }
    }
}
