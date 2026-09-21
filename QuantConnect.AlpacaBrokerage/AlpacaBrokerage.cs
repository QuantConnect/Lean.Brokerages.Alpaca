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
using QuantConnect.Data;
using QuantConnect.Util;
using QuantConnect.Orders;
using QuantConnect.Logging;
using QuantConnect.Interfaces;
using QuantConnect.Lean.Engine.Results;
using QuantConnect.Securities;
using QuantConnect.Orders.Fees;
using System.Collections.Generic;
using AlpacaMarket = Alpaca.Markets;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using QuantConnect.Api;
using QuantConnect.Data.Market;
using System.IO;
using System.Net.NetworkInformation;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using QuantConnect.Configuration;
using QuantConnect.Brokerages.CrossZero;
using System.Collections.Concurrent;
using System.Threading;
using System.Runtime.CompilerServices;
using System.Net.Http;

[assembly: InternalsVisibleTo("QuantConnect.Brokerages.Alpaca.Tests")]

namespace QuantConnect.Brokerages.Alpaca
{
    [BrokerageFactory(typeof(AlpacaBrokerageFactory))]
    public partial class AlpacaBrokerage : Brokerage
    {
        private IDataAggregator _aggregator;

        private IOrderProvider _orderProvider;
        private ISecurityProvider _securityProvider;

        private EventBasedDataQueueHandlerSubscriptionManager _subscriptionManager;

        private ConcurrentDictionary<int, decimal> _orderIdToFillQuantity = new();

        /// <summary>
        /// Holds the legs of a combo order until Lean has sent all of them, because Alpaca takes them as one order.
        /// </summary>
        private readonly GroupOrderCacheManager _groupOrderCacheManager = new();

        private BrokerageConcurrentMessageHandler<ITradeUpdate> _messageHandler;
        private AlpacaBrokerageSymbolMapper _symbolMapper;

        private IAlpacaTradingClient _tradingClient;

        private IAlpacaDataClient _equityHistoricalDataClient;
        private IAlpacaCryptoDataClient _cryptoHistoricalDataClient;
        private IAlpacaOptionsDataClient _optionsHistoricalDataClient;

        private IAlpacaStreamingClient _orderStreamingClient;
        private AlpacaStreamingClientWrapper _equityStreamingClient;
        private AlpacaStreamingClientWrapper _optionsStreamingClient;
        private AlpacaStreamingClientWrapper _cryptoStreamingClient;

        private bool _isInitialized;
        private bool _connected;

        private readonly ManualResetEvent _reconnectionResetEvent = new(false);
        private readonly CancellationTokenSource _cancellationTokenSource = new();

        /// <summary>
        /// Signals when the order <c>trade_updates</c> stream is authorized and
        /// able to deliver fill events. Order operations wait on this so REST
        /// order placement cannot race a dead stream (issue #58).
        /// </summary>
        private readonly ManualResetEventSlim _orderStreamReadyEvent = new(initialState: false);

        /// <summary>
        /// Provides user-facing reason messages for specific trade events.
        /// Used when emitting order events.
        /// </summary>
        private static readonly Dictionary<TradeEvent, string> _tradeEventReason = new()
        {
            { TradeEvent.Expired,  "The order was canceled by the brokerage." }
        };

        /// <summary>
        /// Maps each brokerage order ID to a set of execution IDs, used to detect and skip duplicate trade updates.
        /// </summary>
        internal readonly Dictionary<Guid, HashSet<Guid>> _duplicationExecutionOrderIdByBrokerageOrderId = [];

        /// <summary>
        /// Tracks unsupported TimeInForce values detected during order conversion.
        /// This allows the system to issue a warning for each unsupported TIF only once,
        /// preventing duplicate messages when processing multiple orders.
        /// </summary>
        private readonly HashSet<AlpacaMarket.TimeInForce> _unsupportedTimeInForce = [];

        /// <summary>
        /// Returns true if we're currently connected to the broker
        /// </summary>
        public override bool IsConnected => _connected;

        /// <summary>
        /// Enables concurrent order requests processing
        /// </summary>
        public override bool ConcurrencyEnabled => true;

        /// <summary>
        /// Parameterless constructor for brokerage
        /// </summary>
        public AlpacaBrokerage() : base("Alpaca")
        {
        }

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
        public AlpacaBrokerage(string apiKey, string apiKeySecret, string accessToken, bool isPaperTrading, IAlgorithm algorithm)
            : this(apiKey, apiKeySecret, accessToken, isPaperTrading, algorithm?.Portfolio?.Transactions, algorithm?.Portfolio)
        { }

        /// <summary>
        /// Initializes a new instance of the <see cref="AlpacaBrokerage"/> class.
        /// </summary>
        /// <param name="apiKey">The API key for authentication with Alpaca.</param>
        /// <param name="apiKeySecret">The secret key for authentication with Alpaca.</param>
        /// <param name="isPaperTrading">Indicates whether the brokerage should use the paper trading environment.</param>
        /// <param name="securityProvider">The type capable of fetching the holdings for the specified symbol</param>
        /// <remarks>
        /// This constructor initializes a new instance of the <see cref="AlpacaBrokerage"/> class with the specified API key,
        /// API secret key, a flag indicating whether to use paper trading, and an instance of <see cref="IDataAggregator"/>.
        /// </remarks>
        public AlpacaBrokerage(string apiKey, string apiKeySecret, string accessToken, bool isPaperTrading, IOrderProvider orderProvider, ISecurityProvider securityProvider) : base("Alpaca")
        {
            Initialize(apiKey, apiKeySecret, accessToken, isPaperTrading, orderProvider, securityProvider);
        }

        /// <summary>
        /// Initializes this instance
        /// </summary>
        private void Initialize(string apiKey, string apiKeySecret, string accessToken, bool isPaperTrading, IOrderProvider orderProvider, ISecurityProvider securityProvider)
        {
            if (_isInitialized)
            {
                return;
            }
            _isInitialized = true;
            ValidateSubscription();

            SecurityKey tradingSecretKey = null;
            if (!string.IsNullOrEmpty(accessToken))
            {
                tradingSecretKey = new OAuthKey(accessToken);
            }
            SecretKey secretKey = null;
            if (!string.IsNullOrEmpty(apiKeySecret))
            {
                secretKey = new SecretKey(apiKey, apiKeySecret);
            }

            if (secretKey == null && tradingSecretKey == null)
            {
                // shouldn't happen
                throw new ArgumentException("No valid Alpaca brokerage credentials were provided!");
            }

            _orderProvider = orderProvider;
            _securityProvider = securityProvider;

            var environment = isPaperTrading ? Environments.Paper : Environments.Live;
            // trading api client
            _tradingClient = EnvironmentExtensions.GetAlpacaTradingClient(environment, tradingSecretKey ?? secretKey);
            // order updates
            _orderStreamingClient = EnvironmentExtensions.GetAlpacaStreamingClient(environment, tradingSecretKey ?? secretKey);

            // if we are used as a data queue handler ignore order updates
            if (_orderProvider != null)
            {
                _orderStreamingClient.OnTradeUpdate += (message) => _messageHandler.HandleNewMessage(message);
                WireStreamingClientEvents(_orderStreamingClient);
            }
            _messageHandler = new(HandleTradeUpdate, ConcurrencyEnabled);
            _symbolMapper = new AlpacaBrokerageSymbolMapper(_tradingClient);

            // historical equity
            _equityHistoricalDataClient = EnvironmentExtensions.GetAlpacaDataClient(environment, tradingSecretKey ?? secretKey);

            // historical options
            _optionsHistoricalDataClient = EnvironmentExtensions.GetAlpacaOptionsDataClient(environment, tradingSecretKey ?? secretKey);

            // historical crypto
            _cryptoHistoricalDataClient = EnvironmentExtensions.GetAlpacaCryptoDataClient(environment, tradingSecretKey ?? secretKey);

            if (secretKey != null)
            {
                // equity streaming client
                _equityStreamingClient = new AlpacaStreamingClientWrapper(secretKey, SecurityType.Equity);

                // streaming crypto
                _cryptoStreamingClient = new AlpacaStreamingClientWrapper(secretKey, SecurityType.Crypto);

                // streaming options
                _optionsStreamingClient = new AlpacaStreamingClientWrapper(secretKey, SecurityType.Option);

                foreach (var streamingClient in new IStreamingClient[] { _cryptoStreamingClient, _optionsStreamingClient, _equityStreamingClient })
                {
                    WireStreamingClientEvents(streamingClient);
                }

                _subscriptionManager = new EventBasedDataQueueHandlerSubscriptionManager();
                _subscriptionManager.SubscribeImpl += (s, t) => Subscribe(s);
                _subscriptionManager.UnsubscribeImpl += (s, t) => Unsubscribe(s);

                _aggregator = Composer.Instance.GetPart<IDataAggregator>();
                if (_aggregator == null)
                {
                    // toolbox downloader case
                    var aggregatorName = Config.Get("data-aggregator", "QuantConnect.Lean.Engine.DataFeeds.AggregationManager");
                    Log.Trace($"AlpacaBrokerage.AlpacaBrokerage(): found no data aggregator instance, creating {aggregatorName}");
                    _aggregator = Composer.Instance.GetExportedValueByTypeName<IDataAggregator>(aggregatorName);
                }
            }
            ReconnectionLogic();

            DeploymentDetailsHelper.Add("alpaca-paper-trading", isPaperTrading.ToStringInvariant());
        }

        private void WireStreamingClientEvents(IStreamingClient streamingClient)
        {
            streamingClient.Connected += (obj) => StreamingClient_Connected(streamingClient, obj);
            streamingClient.OnWarning += (obj) => StreamingClient_OnWarning(streamingClient, obj);
            streamingClient.SocketOpened += () => StreamingClient_SocketOpened(streamingClient);
            streamingClient.SocketClosed += () => StreamingClient_SocketClosed(streamingClient);
            streamingClient.OnError += (obj) => StreamingClient_OnError(streamingClient, obj);

            if (streamingClient is AlpacaStreamingClientWrapper wrapper)
            {
                wrapper.EnviromentFailure += (message) => Log.Trace($"AlpacaBrokerage.Initialize(): {message}");
            }
        }

        private void StreamingClient_OnError(IStreamingClient client, Exception obj)
        {
            Log.Trace($"{nameof(StreamingClient_OnError)}({client.GetStreamingClientName()}): {obj}");
        }

        private void StreamingClient_SocketClosed(IStreamingClient client)
        {
            Log.Trace($"{nameof(StreamingClient_SocketClosed)}({client.GetStreamingClientName()}): SocketClosed");
            if (_connected)
            {
                // Order-stream-specific: gate PlaceOrder/UpdateOrder/CancelOrder while the stream is down.
                if (client == _orderStreamingClient)
                {
                    Log.Trace($"{nameof(StreamingClient_SocketClosed)}({client.GetStreamingClientName()}): order stream closed; blocking order operations until reconnect.");
                    _orderStreamReadyEvent.Reset();
                }
                _connected = false;
                // let consumers know, we will try to reconnect internally, if we can't lean will kill us
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Disconnect, "Disconnected", "Brokerage Disconnected"));
                _reconnectionResetEvent.Set();
            }
        }

        private void StreamingClient_SocketOpened(IStreamingClient client)
        {
            Log.Trace($"{nameof(StreamingClient_SocketOpened)}({client.GetStreamingClientName()}): SocketOpened");
        }

        private void StreamingClient_OnWarning(IStreamingClient client, string obj)
        {
            Log.Trace($"{nameof(StreamingClient_OnWarning)}({client.GetStreamingClientName()}): {obj}");
        }

        private void StreamingClient_Connected(IStreamingClient client, AuthStatus obj)
        {
            Log.Trace($"{nameof(StreamingClient_Connected)}({client.GetStreamingClientName()}): {obj}");

            if (client == _orderStreamingClient && obj == AuthStatus.Authorized)
            {
                Log.Trace($"{nameof(StreamingClient_Connected)}({client.GetStreamingClientName()}): order stream authorized; releasing order operations.");
                _orderStreamReadyEvent.Set();
            }
        }

        #region Brokerage

        /// <summary>
        /// Gets all open orders on the account.
        /// NOTE: The order objects returned do not have QC order IDs.
        /// </summary>
        /// <returns>The open orders returned from Alpaca</returns>
        public override List<Order> GetOpenOrders()
        {
            var orders = _tradingClient.ListOrdersAsync(new ListOrdersRequest() { OrderStatusFilter = OrderStatusFilter.Open }).SynchronouslyAwaitTaskResult();

            var leanOrders = new List<Order>();
            foreach (var brokerageOrder in orders)
            {
                if (Log.DebuggingEnabled)
                {
                    Log.Debug($"{nameof(AlpacaBrokerage)}.{nameof(GetOpenOrders)}: {brokerageOrder}"); 
                }

                if (TryConvertToLeanOrders(brokerageOrder, out var convertedOrders))
                {
                    leanOrders.AddRange(convertedOrders);
                }
            }

            return leanOrders;
        }

        /// <summary>
        /// Tells an Alpaca multi-leg options order from the other orders that carry legs (bracket, OCO, OTO):
        /// only the multi-leg order has no symbol of its own. <see cref="IOrder.OrderClass"/> cannot tell them apart,
        /// it reads <see cref="OrderClass.Simple"/> for every order.
        /// </summary>
        /// <param name="brokerageOrder">The Alpaca order.</param>
        /// <returns><c>true</c> when the order is a multi-leg options order; otherwise <c>false</c>.</returns>
        private static bool IsMultiLegOptionsOrder(IOrder brokerageOrder)
        {
            return brokerageOrder.Legs.Count > 1 && string.IsNullOrEmpty(brokerageOrder.Symbol);
        }

        /// <summary>
        /// Converts an Alpaca order to the matching Lean orders: one order for a single order, one combo order per leg
        /// for a multi-leg options order. The combo orders share one <see cref="GroupOrderManager"/> and every one of them
        /// carries the id of the Alpaca order as its brokerage id, because Alpaca reports all legs under that id.
        /// </summary>
        /// <param name="brokerageOrder">The Alpaca order.</param>
        /// <param name="leanOrders">When this method returns <c>true</c>, the Lean orders; otherwise empty.</param>
        /// <returns><c>true</c> when the order was converted; otherwise <c>false</c>.</returns>
        private bool TryConvertToLeanOrders(IOrder brokerageOrder, out List<Order> leanOrders)
        {
            leanOrders = [];

            var orderProperties = new AlpacaOrderProperties();
            if (!orderProperties.TryGetLeanTimeInForceByAlpacaTimeInForce(brokerageOrder.TimeInForce))
            {
                if (_unsupportedTimeInForce.Add(brokerageOrder.TimeInForce))
                {
                    OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, -1, $"Detected unsupported Lean TimeInForce of '{brokerageOrder.TimeInForce}', ignoring. Using default: TimeInForce.GoodTilCanceled"));
                }
            }



            if (!IsMultiLegOptionsOrder(brokerageOrder))
            {
                if (brokerageOrder.Legs.Count > 1)
                {
                    OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "NotSupportedOrderType", "Orders with attached legs (bracket, OCO, OTO) are not currently supported."));
                    return false;
                }

                leanOrders.Add(CreateLeanOrder(brokerageOrder, brokerageOrder, orderProperties));
                _duplicationExecutionOrderIdByBrokerageOrderId[brokerageOrder.OrderId] = [];
                return true;
            }

            // Alpaca marks a credit with a negative limit price; Lean marks it with a negative group quantity and keeps the price positive.
            var groupDirection = brokerageOrder.LimitPrice < 0 ? OrderDirection.Sell : OrderDirection.Buy;
            var groupQuantity = GroupOrderExtensions.GetGroupQuantityByEachLegQuantity(brokerageOrder.Legs.Select(leg => leg.Quantity.Value), groupDirection);

            var groupOrderManager = default(GroupOrderManager);
            switch (brokerageOrder.OrderType)
            {
                case AlpacaMarket.OrderType.Market:
                    groupOrderManager = new GroupOrderManager(brokerageOrder.Legs.Count, groupQuantity);
                    break;
                case AlpacaMarket.OrderType.Limit:
                    groupOrderManager = new GroupOrderManager(brokerageOrder.Legs.Count, groupQuantity, Math.Abs(brokerageOrder.LimitPrice.Value));
                    break;
                default:
                    throw new NotSupportedException($"{nameof(AlpacaBrokerage)}.{nameof(TryConvertToLeanOrders)}: Order type '{brokerageOrder.OrderType}' is not supported for multi-leg orders.");
            }

            foreach (var leg in brokerageOrder.Legs)
            {
                leanOrders.Add(CreateLeanOrder(brokerageOrder, leg, orderProperties, groupOrderManager));
            }

            return true;
        }

        /// <summary>
        /// Creates the Lean order of an Alpaca order, or of one leg of an Alpaca multi-leg options order.
        /// The symbol, the side and the quantities come from the leg; the order type, the prices and the submit time come
        /// from the whole order, because a leg carries none of its own. For a single order the leg is the order itself.
        /// </summary>
        /// <param name="brokerageOrder">The Alpaca order.</param>
        /// <param name="leg">The leg to create the Lean order for; the order itself when it has no legs.</param>
        /// <param name="orderProperties">The order properties of the Lean order.</param>
        /// <param name="groupOrderManager">The group order manager shared by the legs of a multi-leg order; <c>null</c> for a single order.</param>
        /// <returns>The Lean order, with its status and the id of the Alpaca order as its brokerage id.</returns>
        private Order CreateLeanOrder(IOrder brokerageOrder, IOrder leg, AlpacaOrderProperties orderProperties, GroupOrderManager groupOrderManager = null)
        {
            var leanSymbol = _symbolMapper.GetLeanSymbol(leg.AssetClass, leg.Symbol);
            // The side of a multi-leg order itself is not reliable, so the quantity takes its sign from the side of the leg.
            var quantity = leg.OrderSide == OrderSide.Buy ? leg.Quantity.Value : decimal.Negate(leg.Quantity.Value);

            var leanOrder = default(Order);
            switch (brokerageOrder.OrderType)
            {
                case AlpacaMarket.OrderType.Market when groupOrderManager != null:
                    leanOrder = new ComboMarketOrder(leanSymbol, quantity, brokerageOrder.SubmittedAtUtc.Value, groupOrderManager, properties: orderProperties);
                    break;
                case AlpacaMarket.OrderType.Market:

                    switch (brokerageOrder.TimeInForce)
                    {
                        case AlpacaMarket.TimeInForce.Opg:
                            leanOrder = new MarketOnOpenOrder(leanSymbol, quantity, brokerageOrder.SubmittedAtUtc.Value, properties: orderProperties);
                            break;
                        case AlpacaMarket.TimeInForce.Cls:
                            leanOrder = new MarketOnCloseOrder(leanSymbol, quantity, brokerageOrder.SubmittedAtUtc.Value, properties: orderProperties);
                            break;
                        default:
                            leanOrder = new Orders.MarketOrder(leanSymbol, quantity, brokerageOrder.SubmittedAtUtc.Value, properties: orderProperties);
                            break;
                    }
                    break;
                case AlpacaMarket.OrderType.Limit when groupOrderManager != null:
                    leanOrder = new ComboLimitOrder(leanSymbol, quantity, groupOrderManager.LimitPrice, brokerageOrder.SubmittedAtUtc.Value, groupOrderManager, properties: orderProperties);
                    break;
                case AlpacaMarket.OrderType.Limit:
                    leanOrder = new Orders.LimitOrder(leanSymbol, quantity, brokerageOrder.LimitPrice.Value, brokerageOrder.SubmittedAtUtc.Value, properties: orderProperties);
                    break;
                case AlpacaMarket.OrderType.Stop:
                    leanOrder = new StopMarketOrder(leanSymbol, quantity, brokerageOrder.StopPrice.Value, brokerageOrder.SubmittedAtUtc.Value, properties: orderProperties);
                    break;
                case AlpacaMarket.OrderType.StopLimit:
                    leanOrder = new Orders.StopLimitOrder(leanSymbol, quantity, brokerageOrder.StopPrice.Value, brokerageOrder.LimitPrice.Value, brokerageOrder.SubmittedAtUtc.Value, properties: orderProperties);
                    break;
                case AlpacaMarket.OrderType.TrailingStop:
                    var trailingAsPercent = brokerageOrder.TrailOffsetInPercent.HasValue ? true : false;
                    var trailingAmount = brokerageOrder.TrailOffsetInPercent.HasValue ? brokerageOrder.TrailOffsetInPercent.Value / 100m : brokerageOrder.TrailOffsetInDollars.Value;
                    leanOrder = new Orders.TrailingStopOrder(leanSymbol, quantity, brokerageOrder.StopPrice.Value, trailingAmount, trailingAsPercent, brokerageOrder.SubmittedAtUtc.Value, properties: orderProperties);
                    break;
                default:
                    throw new NotSupportedException($"{nameof(AlpacaBrokerage)}.{nameof(GetOpenOrders)}: Order type '{brokerageOrder.OrderType}' is not supported.");
            }

            leanOrder.Status = Orders.OrderStatus.Submitted;
            if (leg.FilledQuantity > 0 && leg.FilledQuantity != leg.Quantity)
            {
                leanOrder.Status = Orders.OrderStatus.PartiallyFilled;
            }

            leanOrder.BrokerId.Add(brokerageOrder.OrderId.ToString());

            return leanOrder;
        }

        /// <summary>
        /// Gets all holdings for the account
        /// </summary>
        /// <returns>The current holdings from the account</returns>
        public override List<Holding> GetAccountHoldings()
        {
            var positions = _tradingClient.ListPositionsAsync().SynchronouslyAwaitTaskResult();

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
            var accounts = _tradingClient.GetAccountAsync().SynchronouslyAwaitTaskResult();
            var balances = new List<CashAmount>() { new(accounts.TradableCash, accounts.Currency) };

            // Alpaca's account endpoint only reports fiat cash. Crypto coin balances are returned as
            // positions, but Lean's CashBook tracks each coin as a currency, so include them here.
            // Otherwise the daily cash sync would not find them and zero out the crypto holdings.
            var positions = _tradingClient.ListPositionsAsync().SynchronouslyAwaitTaskResult();
            foreach (var position in positions)
            {
                if (position.AssetClass != AssetClass.Crypto)
                {
                    continue;
                }

                var leanSymbol = _symbolMapper.GetLeanSymbol(position.AssetClass, position.Symbol);
                if (!CurrencyPairUtil.TryDecomposeCurrencyPair(leanSymbol, out var baseCurrency, out _))
                {
                    OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Error, -1, $"Unable to decompose crypto pair {leanSymbol} into base/quote currencies."));
                    continue;
                }
                balances.Add(new CashAmount(position.Quantity, baseCurrency));  
            }

            return balances;
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

            // Lean sends the legs of a combo one call at a time; nothing goes to Alpaca until the whole group is here.
            if (!_groupOrderCacheManager.TryGetGroupCachedOrders(order, out var orders))
            {
                return true;
            }

            if (orders.Count > 1)
            {
                PlaceMultiLegOrder(orders);
                return true;
            }

            try
            {
                ExecuteWhenReconnectedAndStreamLocked(nameof(PlaceOrder), () =>
                {
                    var holdingQuantity = _securityProvider.GetHoldingsQuantity(order.Symbol);
                    var isPlaceCrossOrder = TryCrossZeroPositionOrder(order, holdingQuantity);
                    if (isPlaceCrossOrder == null)
                    {
                        var orderRequest = order.CreateAlpacaOrder(order.AbsoluteQuantity, _symbolMapper, order.Type);
                        var response = _tradingClient.PostOrderAsync(orderRequest).SynchronouslyAwaitTaskResult();
                        if (response == null || response.OrderStatus == AlpacaMarket.OrderStatus.Rejected)
                        {
                            OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero, $"{nameof(AlpacaBrokerage)} Place Order Failed") { Status = Orders.OrderStatus.Invalid });
                            return;
                        }
                        order.BrokerId.Add(response.OrderId.ToString());

                        OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero, $"{nameof(AlpacaBrokerage)} Order Event") { Status = Orders.OrderStatus.Submitted });
                    }
                });
            }
            catch (Exception ex)
            {
                OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero, ex.Message) { Status = Orders.OrderStatus.Invalid });
            }
            return true;
        }

        /// <summary>
        /// Places the legs of a Lean combo order as one Alpaca multi-leg options order. Every leg gets the id of that
        /// Alpaca order as its brokerage id, because Alpaca reports the updates of all legs under it.
        /// The request and the submitted events run inside the stream lock, so a fill cannot be handled before the legs carry the id.
        /// </summary>
        /// <param name="orders">The Lean combo orders, one per leg, all sharing one group order manager.</param>
        private void PlaceMultiLegOrder(List<Order> orders)
        {
            try
            {
                ExecuteWhenReconnectedAndStreamLocked(nameof(PlaceOrder), () =>
                {
                    var orderRequest = orders.CreateAlpacaMultiLegOrder(_symbolMapper, _securityProvider);
                    var response = _tradingClient.PostOrderAsync(orderRequest).SynchronouslyAwaitTaskResult();
                    if (response == null || response.OrderStatus == AlpacaMarket.OrderStatus.Rejected)
                    {
                        OnOrderEvents(CreateOrderEvents(orders, Orders.OrderStatus.Invalid, $"{nameof(AlpacaBrokerage)} Place Order Failed"));
                        return;
                    }

                    var brokerageOrderId = response.OrderId.ToString();
                    foreach (var order in orders)
                    {
                        order.BrokerId.Add(brokerageOrderId);
                    }

                    OnOrderEvents(CreateOrderEvents(orders, Orders.OrderStatus.Submitted, $"{nameof(AlpacaBrokerage)} Order Event"));
                });
            }
            catch (Exception ex)
            {
                OnOrderEvents(CreateOrderEvents(orders, Orders.OrderStatus.Invalid, ex.Message));
            }
        }

        /// <summary>
        /// Creates one order event with the same status and message for each of the given orders.
        /// The legs of a combo change status together, so their events go to Lean in one batch.
        /// </summary>
        /// <param name="orders">The Lean orders to report.</param>
        /// <param name="status">The new status of the orders.</param>
        /// <param name="message">The message of the order events.</param>
        /// <returns>One order event per order.</returns>
        private static List<OrderEvent> CreateOrderEvents(List<Order> orders, Orders.OrderStatus status, string message)
        {
            var orderEvents = new List<OrderEvent>(orders.Count);
            foreach (var order in orders)
            {
                orderEvents.Add(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero, message) { Status = status });
            }
            return orderEvents;
        }

        internal void HandleTradeUpdate(ITradeUpdate obj)
        {
            try
            {
                // TODO: Revert to Log.Debug when issue #28 is resolved
                Log.Trace($"{nameof(AlpacaBrokerage)}.{nameof(HandleTradeUpdate)}: {obj}");

                var brokerageOrderId = obj.Order.OrderId.ToString();

                // A multi-leg order is one Alpaca order but one Lean order per leg, so the single order lookup below cannot serve it.
                if (IsMultiLegOptionsOrder(obj.Order))
                {
                    HandleMultiLegTradeUpdate(obj, _orderProvider.GetOrdersByBrokerageId(brokerageOrderId));
                    return;
                }

                var newLeanOrderStatus = GetOrderStatus(obj.Event);
                if (!TryGetOrRemoveCrossZeroOrder(brokerageOrderId, newLeanOrderStatus, out var leanOrder))
                {
                    leanOrder = _orderProvider.GetOrdersByBrokerageId(brokerageOrderId)?.SingleOrDefault();
                    if (leanOrder == null && TryConvertToLeanOrders(obj.Order, out var convertedOrders))
                    {
                        leanOrder = convertedOrders[0];
                        OnNewBrokerageOrderNotification(new(leanOrder));

                        if (leanOrder.Id != 0)
                        {
                            OnOrderEvent(new OrderEvent(leanOrder, DateTime.UtcNow, OrderFee.Zero, $"Order was submitted outside Lean")
                            { Status = Orders.OrderStatus.Submitted });

                            if (newLeanOrderStatus == Orders.OrderStatus.Submitted)
                            {
                                return;
                            }
                        }
                        else
                        {
                            leanOrder = null;
                        }
                    }
                }
                if (leanOrder == null)
                {
                    Log.Error($"{nameof(AlpacaBrokerage)}.{nameof(HandleTradeUpdate)}: order id not found: {obj.Order.OrderId}");
                    return;
                }

                // Alpaca can replay trade updates (new/fill) after a terminal event. Once the Lean
                // order is in a closed state, discard any further updates to avoid duplicate events.
                if (leanOrder.Status.IsClosed())
                {
                    return;
                }

                switch (obj.Event)
                {
                    case TradeEvent.New:
                    case TradeEvent.PendingNew:
                        // we don't send anything for this event
                        _duplicationExecutionOrderIdByBrokerageOrderId.TryAdd(obj.Order.OrderId, []);
                        return;
                    case TradeEvent.Rejected:
                    case TradeEvent.Canceled:
                    case TradeEvent.Replaced:
                    case TradeEvent.Expired:
                        if (_duplicationExecutionOrderIdByBrokerageOrderId.Remove(obj.Order.OrderId))
                        {
                            if (newLeanOrderStatus == Orders.OrderStatus.UpdateSubmitted)
                            {
                                var replacedBrokerageOrderId = obj.Order.ReplacedByOrderId.Value;
                                // If the order already exists in the BrokerId list, it means the update was initiated by Lean.
                                // Otherwise, the order was updated outside of Lean and we need to notify about the new brokerage ID.
                                if (!leanOrder.BrokerId.Contains(replacedBrokerageOrderId.ToString()))
                                {
                                    OnOrderIdChangedEvent(new() { OrderId = leanOrder.Id, BrokerId = [replacedBrokerageOrderId.ToString()] });
                                }
                                _duplicationExecutionOrderIdByBrokerageOrderId[replacedBrokerageOrderId] = [];
                            }

                            if (!_tradeEventReason.TryGetValue(obj.Event, out var message))
                            {
                                message = $"{nameof(AlpacaBrokerage)} Order Event";
                            }

                            OnOrderEvent(new OrderEvent(leanOrder, DateTime.UtcNow, OrderFee.Zero, message) { Status = newLeanOrderStatus });
                        }
                        return;
                    case TradeEvent.Fill:
                        if (_duplicationExecutionOrderIdByBrokerageOrderId.Remove(obj.Order.OrderId))
                        {
                            break;
                        }
                        return;
                    case TradeEvent.PartialFill:
                        if (_duplicationExecutionOrderIdByBrokerageOrderId[obj.Order.OrderId].Add(obj.ExecutionId.Value))
                        {
                            break;
                        }
                        return;
                    case TradeEvent.Accepted:
                    case TradeEvent.PendingReplace:
                    case TradeEvent.PendingCancel:
                        // Skip this event to avoid flooding logs
                        return;
                    default:
                        Log.Trace($"{nameof(AlpacaBrokerage)}.{nameof(HandleTradeUpdate)}.Event: {obj.Event}. TradeUpdate: {obj}");
                        return;
                }

                var leanSymbol = _symbolMapper.GetLeanSymbol(obj.Order.AssetClass, obj.Order.Symbol);

                // alpaca sends the accumulative filled quantity but we need the partial amount for our event
                _orderIdToFillQuantity.TryGetValue(leanOrder.Id, out var previouslyFilledAmount);
                var accumulativeFilledQuantity = _orderIdToFillQuantity[leanOrder.Id] =
                    obj.Order.OrderSide == OrderSide.Buy ? obj.Order.FilledQuantity : decimal.Negate(obj.Order.FilledQuantity);

                if (newLeanOrderStatus.IsClosed())
                {
                    // cleanup
                    _orderIdToFillQuantity.TryRemove(leanOrder.Id, out _);
                }

                var fee = new OrderFee(new CashAmount(0, Currencies.USD));
                if (newLeanOrderStatus == Orders.OrderStatus.Filled)
                {
                    var security = _securityProvider.GetSecurity(leanOrder.Symbol);
                    fee = security.FeeModel.GetOrderFee(new OrderFeeParameters(security, leanOrder));
                }

                var orderEvent = new OrderEvent(leanOrder, obj.TimestampUtc.HasValue ? obj.TimestampUtc.Value : DateTime.UtcNow, fee)
                {
                    Status = newLeanOrderStatus,
                    FillPrice = obj.Price ?? 0m,
                    FillQuantity = accumulativeFilledQuantity - previouslyFilledAmount,
                };

                // if we filled the order and have another contingent order waiting, submit it
                if (!TryHandleRemainingCrossZeroOrder(leanOrder, orderEvent))
                {
                    OnOrderEvent(orderEvent);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"TradeUpdate: {obj}");
                throw;
            }
        }

        /// <summary>
        /// Handles a trade update of an Alpaca multi-leg options order. The update comes for the whole order,
        /// and each of its legs is reported to Lean through the combo order of that leg.
        /// </summary>
        /// <param name="obj">The trade update of the multi-leg order.</param>
        /// <param name="leanOrders">The Lean combo orders that carry the id of the multi-leg order; empty when Lean does not know the order.</param>
        private void HandleMultiLegTradeUpdate(ITradeUpdate obj, List<Order> leanOrders)
        {
            var newLeanOrderStatus = GetOrderStatus(obj.Event);
            if (leanOrders.Count == 0)
            {
                // An outside order first seen on a closing event would be offered to the algorithm only to close at once.
                if (newLeanOrderStatus is not (Orders.OrderStatus.Submitted or Orders.OrderStatus.PartiallyFilled or Orders.OrderStatus.Filled)
                    || !TryConvertToLeanOrders(obj.Order, out leanOrders))
                {
                    return;
                }

                foreach (var leanOrder in leanOrders)
                {
                    OnNewBrokerageOrderNotification(new(leanOrder));
                    if (leanOrder.Id == 0)
                    {
                        return;
                    }
                }

                OnOrderEvents(CreateOrderEvents(leanOrders, Orders.OrderStatus.Submitted, "Order was submitted outside Lean"));

                if (newLeanOrderStatus == Orders.OrderStatus.Submitted)
                {
                    return;
                }
            }

            switch (obj.Event)
            {
                case TradeEvent.Rejected:
                case TradeEvent.Canceled:
                case TradeEvent.Replaced:
                case TradeEvent.Expired:
                    var openLeanOrders = new List<Order>(leanOrders.Count);
                    foreach (var leanOrder in leanOrders)
                    {
                        // Alpaca can replay a trade update after a terminal event; a closed leg must not get a second event.
                        if (leanOrder.Status.IsClosed())
                        {
                            continue;
                        }

                        if (newLeanOrderStatus == Orders.OrderStatus.UpdateSubmitted)
                        {
                            var replacedBrokerageOrderId = obj.Order.ReplacedByOrderId.Value.ToString();
                            // If the order already exists in the BrokerId list, it means the update was initiated by Lean.
                            // Otherwise, the order was updated outside of Lean and we need to notify about the new brokerage ID.
                            if (!leanOrder.BrokerId.Contains(replacedBrokerageOrderId))
                            {
                                OnOrderIdChangedEvent(new() { OrderId = leanOrder.Id, BrokerId = [replacedBrokerageOrderId] });
                            }
                        }

                        openLeanOrders.Add(leanOrder);
                    }

                    if (openLeanOrders.Count > 0)
                    {
                        if (!_tradeEventReason.TryGetValue(obj.Event, out var message))
                        {
                            message = $"{nameof(AlpacaBrokerage)} Order Event";
                        }

                        OnOrderEvents(CreateOrderEvents(openLeanOrders, newLeanOrderStatus, message));
                    }
                    return;
                case TradeEvent.Fill:
                case TradeEvent.PartialFill:
                    var fillEvents = CreateMultiLegFillEvents(obj, leanOrders);
                    if (fillEvents.Count > 0)
                    {
                        OnOrderEvents(fillEvents);
                    }
                    return;
                case TradeEvent.New:
                case TradeEvent.PendingNew:
                case TradeEvent.Accepted:
                case TradeEvent.PendingReplace:
                case TradeEvent.PendingCancel:
                    // we don't send anything for these events
                    return;
                default:
                    Log.Trace($"{nameof(AlpacaBrokerage)}.{nameof(HandleMultiLegTradeUpdate)}.Event: {obj.Event}. TradeUpdate: {obj}");
                    return;
            }
        }

        /// <summary>
        /// Creates the fill events of the legs of a multi-leg order from one trade update.
        /// The update carries no execution id and only the net price of the whole order, so the fill of each leg
        /// is worked out from the filled quantity and the average fill price Alpaca reports on that leg.
        /// </summary>
        /// <param name="obj">The fill or partial fill trade update of the multi-leg order.</param>
        /// <param name="leanOrders">The Lean combo orders of the legs.</param>
        /// <returns>One fill event for each leg whose filled quantity grew; empty when the update brings nothing new.</returns>
        private List<OrderEvent> CreateMultiLegFillEvents(ITradeUpdate obj, List<Order> leanOrders)
        {
            var fillEvents = new List<OrderEvent>(leanOrders.Count);
            foreach (var leg in obj.Order.Legs)
            {
                var leanSymbol = _symbolMapper.GetLeanSymbol(leg.AssetClass, leg.Symbol);
                var leanOrder = leanOrders.FirstOrDefault(order => order.Symbol == leanSymbol);
                if (leanOrder == null)
                {
                    Log.Error($"{nameof(AlpacaBrokerage)}.{nameof(CreateMultiLegFillEvents)}: no Lean order found for the leg '{leg.Symbol}' of the multi-leg order {obj.Order.OrderId}");
                    continue;
                }

                // Alpaca can replay a trade update after a terminal event; a closed leg must not get a second fill.
                if (leanOrder.Status.IsClosed())
                {
                    continue;
                }

                // Alpaca sends the running totals of the leg; what Lean was already told is on the order ticket.
                var orderTicket = _orderProvider.GetOrderTicket(leanOrder.Id);
                var previouslyFilledQuantity = orderTicket?.QuantityFilled ?? 0m;
                var accumulativeFilledQuantity = leg.OrderSide == OrderSide.Buy ? leg.FilledQuantity : decimal.Negate(leg.FilledQuantity);
                var fillQuantity = accumulativeFilledQuantity - previouslyFilledQuantity;

                // Without an execution id, an unchanged filled quantity is the only sign of a replayed update or of a leg that did not trade this time.
                if (fillQuantity == 0)
                {
                    continue;
                }

                if (!leg.AverageFillPrice.HasValue)
                {
                    Log.Error($"{nameof(AlpacaBrokerage)}.{nameof(CreateMultiLegFillEvents)}: the leg '{leg.Symbol}' of the multi-leg order {obj.Order.OrderId} shows a fill without an average fill price. TradeUpdate: {obj}");
                    continue;
                }

                // Alpaca gives the average price of everything filled so far; the price of this fill alone comes from the change of that average.
                var previousAverageFillPrice = orderTicket?.AverageFillPrice ?? 0m;
                var fillPrice = (leg.AverageFillPrice.Value * accumulativeFilledQuantity - previousAverageFillPrice * previouslyFilledQuantity) / fillQuantity;

                var status = Orders.OrderStatus.PartiallyFilled;
                var fee = new OrderFee(new CashAmount(0, Currencies.USD));
                if (accumulativeFilledQuantity == leanOrder.Quantity)
                {
                    status = Orders.OrderStatus.Filled;

                    var security = _securityProvider.GetSecurity(leanOrder.Symbol);
                    fee = security.FeeModel.GetOrderFee(new OrderFeeParameters(security, leanOrder));
                }

                fillEvents.Add(new OrderEvent(leanOrder, obj.TimestampUtc.HasValue ? obj.TimestampUtc.Value : DateTime.UtcNow, fee)
                {
                    Status = status,
                    FillPrice = fillPrice,
                    FillQuantity = fillQuantity
                });
            }

            return fillEvents;
        }

        /// <summary>
        /// Updates the order with the same id
        /// </summary>
        /// <param name="order">The new order information</param>
        /// <returns>True if the request was made for the order to be updated, false otherwise</returns>
        public override bool UpdateOrder(Order order)
        {
            // The legs of a combo share one Alpaca order, so the change goes out once, when Lean has sent every leg.
            if (!_groupOrderCacheManager.TryGetGroupCachedOrders(order, out var orders))
            {
                return true;
            }

            if (orders.Count > 1)
            {
                return UpdateMultiLegOrder(orders);
            }

            if (!TryGetUpdateCrossZeroOrderQuantity(order, out var orderQuantity))
            {
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, -1, $"{nameof(AlpacaBrokerage)}.{nameof(UpdateOrder)}: Unable to modify order quantities."));
                return false;
            }

            var brokerageOrderId = order.BrokerId.Last();
            var pathOrderRequest = new ChangeOrderRequest(new Guid(brokerageOrderId)) { Quantity = Convert.ToInt64(Math.Abs(orderQuantity)) };

            switch (order)
            {
                case Orders.LimitOrder lo:
                    pathOrderRequest.LimitPrice = lo.LimitPrice;
                    break;
                case Orders.TrailingStopOrder sto:
                    pathOrderRequest.Trail = AlpacaBrokerageExtensions.GetTrailOffsetValue(sto).Value;
                    break;
                case StopMarketOrder smo:
                    pathOrderRequest.StopPrice = smo.StopPrice;
                    break;
                case Orders.StopLimitOrder slo:
                    pathOrderRequest.LimitPrice = slo.LimitPrice;
                    pathOrderRequest.StopPrice = slo.StopPrice;
                    break;
            }

            try
            {
                IOrder response = null;
                ExecuteWhenReconnectedAndStreamLocked(nameof(UpdateOrder), () =>
                {
                    response = _tradingClient.PatchOrderAsync(pathOrderRequest).SynchronouslyAwaitTaskResult();
                    if (response == null || response.OrderStatus == AlpacaMarket.OrderStatus.Rejected)
                    {
                        OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero, $"{nameof(AlpacaBrokerage)} Order Event") { Status = Orders.OrderStatus.Invalid });
                        return;
                    }

                    var brokerageOrderId = response.OrderId.ToString();
                    if (!order.BrokerId.Contains(brokerageOrderId))
                    {
                        order.BrokerId.Add(brokerageOrderId);
                    }
                });
                return response != null && response.OrderStatus != AlpacaMarket.OrderStatus.Rejected;
            }
            catch (Exception ex)
            {
                OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero, ex.Message) { Status = Orders.OrderStatus.Invalid });
                return false;
            }
        }

        /// <summary>
        /// Changes the quantity and the limit price of the Alpaca multi-leg options order behind the legs of a Lean combo order.
        /// Alpaca replaces the order with a new one, so every leg gets the id of the new order as well;
        /// the update submitted events come later, with the replaced trade update of the old order.
        /// </summary>
        /// <param name="orders">The Lean combo orders, one per leg, all sharing one group order manager.</param>
        /// <returns>True if Alpaca took the change, false otherwise</returns>
        private bool UpdateMultiLegOrder(List<Order> orders)
        {
            var brokerageOrderId = orders[0].BrokerId.Last();
            try
            {
                IOrder response = null;
                ExecuteWhenReconnectedAndStreamLocked(nameof(UpdateOrder), () =>
                {
                    var changeOrderRequest = orders.CreateAlpacaMultiLegChangeOrder(new Guid(brokerageOrderId));
                    response = _tradingClient.PatchOrderAsync(changeOrderRequest).SynchronouslyAwaitTaskResult();

                    var newBrokerageOrderId = response.OrderId.ToString();
                    foreach (var order in orders)
                    {
                        if (!order.BrokerId.Contains(newBrokerageOrderId))
                        {
                            order.BrokerId.Add(newBrokerageOrderId);
                        }
                    }
                });
                return true;
            }
            catch (Exception ex)
            {
                // A refused change leaves the order open at Alpaca as it was, so the legs must stay open in Lean too.
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "UpdateOrderFailed", $"Update of the multi-leg order {brokerageOrderId} failed: {ex.Message}"));
                return false;
            }
        }

        /// <summary>
        /// Cancels the order with the specified ID
        /// </summary>
        /// <param name="order">The order to cancel</param>
        /// <returns>True if the request was made for the order to be canceled, false otherwise</returns>
        public override bool CancelOrder(Order order)
        {
            // The legs of a combo share one Alpaca order, so the cancel request goes out once, when Lean has asked to cancel every leg.
            if (!_groupOrderCacheManager.TryGetGroupCachedOrders(order, out _))
            {
                return true;
            }

            if (order.Status == Orders.OrderStatus.Filled)
            {
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, -1, "Order already filled"));
                return false;
            }

            if (order.Status is Orders.OrderStatus.Canceled)
            {
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, -1, "Order already canceled"));
                return false;
            }

            try
            {
                var response = false;
                ExecuteWhenReconnectedAndStreamLocked(nameof(CancelOrder), () =>
                {
                    var brokerageOrderId = new Guid(order.BrokerId.Last());
                    response = _tradingClient.CancelOrderAsync(brokerageOrderId).SynchronouslyAwaitTaskResult();
                });
                return response;
            }
            catch (Exception ex)
            {
                OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero, $"Cancel order {order.Id} failed: {ex.Message}") { Status = Orders.OrderStatus.Invalid });
                return false;
            }
        }

        /// <summary>
        /// Places an order that crosses zero (transitions from a short position to a long position or vice versa) and returns the response.
        /// This method implements brokerage-specific logic for placing such orders using Tradier brokerage.
        /// </summary>
        /// <param name="crossZeroOrderRequest">The request object containing details of the cross zero order to be placed.</param>
        /// <param name="isPlaceOrderWithLeanEvent">
        /// A boolean indicating whether the order should be placed with triggering a Lean event.
        /// Default is <c>true</c>, meaning Lean events will be triggered.
        /// </param>
        /// <returns>
        /// A <see cref="CrossZeroOrderResponse"/> object indicating the result of the order placement.
        /// </returns>
        protected override CrossZeroOrderResponse PlaceCrossZeroOrder(CrossZeroFirstOrderRequest crossZeroOrderRequest, bool isPlaceOrderWithLeanEvent)
        {
            var orderRequest = crossZeroOrderRequest.LeanOrder.CreateAlpacaOrder(crossZeroOrderRequest.AbsoluteOrderQuantity, _symbolMapper, crossZeroOrderRequest.OrderType);
            var response = _tradingClient.PostOrderAsync(orderRequest).SynchronouslyAwaitTaskResult();
            if (response == null || response.OrderStatus == AlpacaMarket.OrderStatus.Rejected)
            {
                return new CrossZeroOrderResponse(string.Empty, false);
            }

            var newBrokerageOrderId = response.OrderId.ToString();
            if (!crossZeroOrderRequest.LeanOrder.BrokerId.Contains(newBrokerageOrderId))
            {
                crossZeroOrderRequest.LeanOrder.BrokerId.Add(newBrokerageOrderId);
            }

            if (isPlaceOrderWithLeanEvent)
            {
                OnOrderEvent(new OrderEvent(crossZeroOrderRequest.LeanOrder, DateTime.UtcNow, OrderFee.Zero, $"{nameof(AlpacaBrokerage)} Order Event") { Status = Orders.OrderStatus.Submitted });
            }
            return new CrossZeroOrderResponse(newBrokerageOrderId, true);
        }

        /// <summary>
        /// Connects the client to the broker's remote servers
        /// </summary>
        public override void Connect()
        {
            if (_connected)
            {
                return;
            }

            ConnectAndAuthenticate(_orderStreamingClient);
            _connected = true;
        }

        /// <summary>
        /// Connects to the specified streaming client and authenticates synchronously.
        /// Throws <see cref="InvalidOperationException"/> if authentication fails.
        /// </summary>
        /// <param name="client">The streaming client to connect and authenticate.</param>
        private static void ConnectAndAuthenticate(IStreamingClient streamingClient)
        {
            if (streamingClient is AlpacaStreamingClientWrapper s && s.IsOpenAndAuthorized)
            {
                return;
            }

            var authorizedStatus = streamingClient.ConnectAndAuthenticateAsync().SynchronouslyAwaitTaskResult();
            if (authorizedStatus != AuthStatus.Authorized)
            {
                throw new InvalidOperationException($"Connect(): Failed to connect to {streamingClient.GetStreamingClientName()}. Status: {authorizedStatus}");
            }
        }

        /// <summary>
        /// Runs <paramref name="action"/> through <see cref="BrokerageConcurrentMessageHandler{T}.WithLockedStream"/>,
        /// but first waits for the order <c>trade_updates</c> stream to be authorized.
        /// Prevents REST order placement from racing a dead stream and losing
        /// subsequent fill events (issue #58).
        /// </summary>
        private void ExecuteWhenReconnectedAndStreamLocked(string methodName, Action action)
        {
            if (!_orderStreamReadyEvent.IsSet)
            {
                Log.Trace($"{nameof(AlpacaBrokerage)}.{nameof(ExecuteWhenReconnectedAndStreamLocked)}.{methodName}: waiting for order stream reconnect...");
                try
                {
                    if (!_orderStreamReadyEvent.Wait(TimeSpan.FromMinutes(10), _cancellationTokenSource.Token))
                    {
                        Log.Error($"{nameof(AlpacaBrokerage)}.{nameof(ExecuteWhenReconnectedAndStreamLocked)}.{methodName}: order stream not ready after 10 minutes; skipping {methodName}.");
                        return;
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                Log.Trace($"{nameof(AlpacaBrokerage)}.{nameof(ExecuteWhenReconnectedAndStreamLocked)}.{methodName}: order stream ready.");
            }
            _messageHandler.WithLockedStream(action);
        }

        private void ReconnectionLogic()
        {
            Task.Factory.StartNew(() =>
            {
                Log.Trace($"{nameof(AlpacaBrokerage)}.{nameof(ReconnectionLogic)}: Starting reconnection loop.");
                var attempt = 0;
                while (!_cancellationTokenSource.IsCancellationRequested)
                {
                    _reconnectionResetEvent.WaitOne(_cancellationTokenSource.Token);

                    var delay = TimeSpan.FromSeconds(5 * ++attempt);
                    Log.Trace($"{nameof(AlpacaBrokerage)}.{nameof(ReconnectionLogic)}: attempt #{attempt}, waiting {delay.TotalSeconds}s...");
                    // The server enforces a 90-second timeout for "partially dead" connections.
                    // If another WebSocket connection is opened with the same API key/secret
                    // before the old one is fully closed, this may trigger the
                    // "Too many connections" error. Waiting here prevents premature reconnection
                    // attempts that would conflict with the server's timeout window.
                    if (_cancellationTokenSource.Token.WaitHandle.WaitOne(delay))
                    {
                        break;
                    }

                    _reconnectionResetEvent.Reset();

                    try
                    {
                        Connect();

                        if (!IsConnected)
                        {
                            _reconnectionResetEvent.Set();
                        }
                        else
                        {
                            // if we are used as a brokerage ignore data queue handler updates
                            if (_subscriptionManager != null)
                            {
                                // resubscribe
                                var symbols = _subscriptionManager.GetSubscribedSymbols();
                                Unsubscribe(symbols);
                                Subscribe(symbols);
                            }
                            attempt = 0;
                            // let consumers know we are reconnected, avoid lean killing us
                            OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Reconnect, "Reconnected", "Brokerage Reconnected"));
                        }
                    }
                    catch (Exception ex)
                    {
                        _reconnectionResetEvent.Set();
                        Log.Error(ex);
                    }
                }
                Log.Trace($"{nameof(AlpacaBrokerage)}.{nameof(ReconnectionLogic)}: Reconnection loop ended.");
                _reconnectionResetEvent?.DisposeSafely();
            }, _cancellationTokenSource.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        /// <summary>
        /// Disconnects the client from the broker's remote servers
        /// </summary>
        public override void Disconnect()
        {
            _orderStreamingClient?.DisconnectAsync()?.SynchronouslyAwaitTask();
            _equityStreamingClient?.DisconnectAsync()?.SynchronouslyAwaitTask();
            _cryptoStreamingClient?.DisconnectAsync()?.SynchronouslyAwaitTask();
            _optionsStreamingClient?.DisconnectAsync()?.SynchronouslyAwaitTask();
        }

        public override void Dispose()
        {
            _cancellationTokenSource?.Cancel();
            _cancellationTokenSource?.DisposeSafely();
            _orderStreamReadyEvent?.DisposeSafely();

            _tradingClient.DisposeSafely();

            _equityHistoricalDataClient.DisposeSafely();
            _cryptoHistoricalDataClient.DisposeSafely();
            _optionsHistoricalDataClient.DisposeSafely();

            // streaming
            _orderStreamingClient.DisposeSafely();
            _equityStreamingClient.DisposeSafely();
            _cryptoStreamingClient.DisposeSafely();
            _optionsStreamingClient.DisposeSafely();
        }

        /// <summary>
        /// Gets the latest market quote for the specified symbol.
        /// </summary>
        /// <param name="symbol">The symbol for which to get the latest quote.</param>
        /// <returns>The latest quote for the specified symbol.</returns>
        /// <exception cref="NotSupportedException">Thrown when the symbol's security type is not supported.</exception>
        /// <exception cref="Exception">Thrown when an error occurs while fetching the quote.</exception>
        protected IQuote GetLatestQuote(Symbol symbol)
        {
            var brokerageSymbol = _symbolMapper.GetBrokerageSymbol(symbol);
            switch (symbol.SecurityType)
            {
                case SecurityType.Equity:
                    return _equityHistoricalDataClient.GetLatestQuoteAsync(new LatestMarketDataRequest(brokerageSymbol)).SynchronouslyAwaitTaskResult();
                case SecurityType.Option:
                    return _optionsHistoricalDataClient.ListLatestQuotesAsync(new LatestOptionsDataRequest(new string[] { brokerageSymbol })).SynchronouslyAwaitTaskResult()[brokerageSymbol];
                case SecurityType.Crypto:
                    return _cryptoHistoricalDataClient.ListLatestQuotesAsync(new LatestDataListRequest(new string[] { brokerageSymbol })).SynchronouslyAwaitTaskResult()[brokerageSymbol];
                default:
                    throw new NotSupportedException($"{nameof(AlpacaBrokerage)}.{nameof(GetLatestQuote)}: Security type {symbol.SecurityType} is not supported.");
            }
        }

        private static Orders.OrderStatus GetOrderStatus(TradeEvent tradeEvent)
        {
            switch (tradeEvent)
            {
                case TradeEvent.PendingNew:
                    return Orders.OrderStatus.New;
                case TradeEvent.New:
                    return Orders.OrderStatus.Submitted;
                case TradeEvent.Rejected:
                    return Orders.OrderStatus.Invalid;
                case TradeEvent.Canceled:
                    return Orders.OrderStatus.Canceled;
                case TradeEvent.Replaced:
                    return Orders.OrderStatus.UpdateSubmitted;
                case TradeEvent.Fill:
                    return Orders.OrderStatus.Filled;
                case TradeEvent.PartialFill:
                    return Orders.OrderStatus.PartiallyFilled;
                case TradeEvent.Expired:
                    return Orders.OrderStatus.Canceled;
                default:
                    return Orders.OrderStatus.New;
            }
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

        private class SubscriptionEntry
        {
            public Symbol Symbol { get; set; }
            public decimal PriceMagnifier { get; set; }
            public Tick LastTradeTick { get; set; }
            public Tick LastQuoteTick { get; set; }
            public Tick LastOpenInterestTick { get; set; }
        }

        private class ModulesReadLicenseRead : Api.RestResponse
        {
            [JsonProperty(PropertyName = "license")]
            public string License;
            [JsonProperty(PropertyName = "organizationId")]
            public string OrganizationId;
        }

        /// <summary>
        /// Validate the user of this project has permission to be using it via our web API.
        /// </summary>
        private static void ValidateSubscription()
        {
            try
            {
                var productId = 347;
                var userId = Globals.UserId;
                var token = Globals.UserToken;
                var organizationId = Globals.OrganizationID;
                // Verify we can authenticate with this user and token
                var api = new ApiConnection(userId, token);
                if (!api.Connected)
                {
                    throw new ArgumentException("Invalid api user id or token, cannot authenticate subscription.");
                }
                // Compile the information we want to send when validating
                var information = new Dictionary<string, object>()
                {
                    {"productId", productId},
                    {"machineName", Environment.MachineName},
                    {"userName", Environment.UserName},
                    {"domainName", Environment.UserDomainName},
                    {"os", Environment.OSVersion}
                };
                // IP and Mac Address Information
                try
                {
                    var interfaceDictionary = new List<Dictionary<string, object>>();
                    foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(nic => nic.OperationalStatus == OperationalStatus.Up))
                    {
                        var interfaceInformation = new Dictionary<string, object>();
                        // Get UnicastAddresses
                        var addresses = nic.GetIPProperties().UnicastAddresses
                            .Select(uniAddress => uniAddress.Address)
                            .Where(address => !IPAddress.IsLoopback(address)).Select(x => x.ToString());
                        // If this interface has non-loopback addresses, we will include it
                        if (!addresses.IsNullOrEmpty())
                        {
                            interfaceInformation.Add("unicastAddresses", addresses);
                            // Get MAC address
                            interfaceInformation.Add("MAC", nic.GetPhysicalAddress().ToString());
                            // Add Interface name
                            interfaceInformation.Add("name", nic.Name);
                            // Add these to our dictionary
                            interfaceDictionary.Add(interfaceInformation);
                        }
                    }
                    information.Add("networkInterfaces", interfaceDictionary);
                }
                catch (Exception)
                {
                    // NOP, not necessary to crash if fails to extract and add this information
                }
                // Include our OrganizationId is specified
                if (!string.IsNullOrEmpty(organizationId))
                {
                    information.Add("organizationId", organizationId);
                }
                // Create HTTP request
                using var request = ApiUtils.CreateJsonPostRequest("modules/license/read", information);
                api.TryRequest(request, out ModulesReadLicenseRead result);
                if (!result.Success)
                {
                    throw new InvalidOperationException($"Request for subscriptions from web failed, Response Errors : {string.Join(',', result.Errors)}");
                }

                var encryptedData = result.License;
                // Decrypt the data we received
                DateTime? expirationDate = null;
                long? stamp = null;
                bool? isValid = null;
                if (encryptedData != null)
                {
                    // Fetch the org id from the response if we are null, we need it to generate our validation key
                    if (string.IsNullOrEmpty(organizationId))
                    {
                        organizationId = result.OrganizationId;
                    }
                    // Create our combination key
                    var password = $"{token}-{organizationId}";
                    var key = SHA256.HashData(Encoding.UTF8.GetBytes(password));
                    // Split the data
                    var info = encryptedData.Split("::");
                    var buffer = Convert.FromBase64String(info[0]);
                    var iv = Convert.FromBase64String(info[1]);
                    // Decrypt our information
                    using var aes = new AesManaged();
                    var decryptor = aes.CreateDecryptor(key, iv);
                    using var memoryStream = new MemoryStream(buffer);
                    using var cryptoStream = new CryptoStream(memoryStream, decryptor, CryptoStreamMode.Read);
                    using var streamReader = new StreamReader(cryptoStream);
                    var decryptedData = streamReader.ReadToEnd();
                    if (!decryptedData.IsNullOrEmpty())
                    {
                        var jsonInfo = JsonConvert.DeserializeObject<JObject>(decryptedData);
                        expirationDate = jsonInfo["expiration"]?.Value<DateTime>();
                        isValid = jsonInfo["isValid"]?.Value<bool>();
                        stamp = jsonInfo["stamped"]?.Value<int>();
                    }
                }
                // Validate our conditions
                if (!expirationDate.HasValue || !isValid.HasValue || !stamp.HasValue)
                {
                    throw new InvalidOperationException("Failed to validate subscription.");
                }

                var nowUtc = DateTime.UtcNow;
                var timeSpan = nowUtc - Time.UnixTimeStampToDateTime(stamp.Value);
                if (timeSpan > TimeSpan.FromHours(12))
                {
                    throw new InvalidOperationException("Invalid API response.");
                }
                if (!isValid.Value)
                {
                    throw new ArgumentException($"Your subscription is not valid, please check your product subscriptions on our website.");
                }
                if (expirationDate < nowUtc)
                {
                    throw new ArgumentException($"Your subscription expired {expirationDate}, please renew in order to use this product.");
                }
            }
            catch (Exception e)
            {
                Log.Error($"ValidateSubscription(): Failed during validation, shutting down. Error : {e.Message}");
                Environment.Exit(1);
            }
        }
    }
}