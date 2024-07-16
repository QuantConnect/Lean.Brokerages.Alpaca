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
using QuantConnect.Logging;
using QuantConnect.Interfaces;
using QuantConnect.Securities;
using QuantConnect.Orders.Fees;
using System.Collections.Generic;
using AlpacaMarket = Alpaca.Markets;
using System.Collections.Concurrent;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using QuantConnect.Api;
using QuantConnect.Data.Market;
using RestSharp;
using System.IO;
using System.Net.NetworkInformation;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using QuantConnect.Configuration;

namespace QuantConnect.Brokerages.Alpaca
{
    [BrokerageFactory(typeof(AlpacaBrokerageFactory))]
    public partial class AlpacaBrokerage : Brokerage
    {
        private IDataAggregator _aggregator;

        private IOrderProvider _orderProvider;

        private EventBasedDataQueueHandlerSubscriptionManager _subscriptionManager;

        private AlpacaBrokerageSymbolMapper _symbolMapper;

        private IAlpacaTradingClient _tradingClient;

        private IAlpacaDataClient _equityHistoricalDataClient;
        private IAlpacaCryptoDataClient _cryptoHistoricalDataClient;
        private IAlpacaOptionsDataClient _optionsHistoricalDataClient;

        private IAlpacaStreamingClient _orderStreamingClient;
        private IAlpacaDataStreamingClient _equityStreamingClient;
        private IAlpacaCryptoStreamingClient _cryptoStreamingClient;

        private bool _isInitialized;

        /// <summary>
        /// Represents an object used for locking to ensure thread safety.
        /// </summary>
        private object _lockObject = new();

        /// <summary>
        /// A concurrent dictionary that maps brokerage order IDs to their respective <see cref="AutoResetEvent"/> instances.
        /// </summary>
        private readonly ConcurrentDictionary<Guid, ManualResetEvent> _resetEventByBrokerageOrderID = new();

        /// <summary>
        /// Returns true if we're currently connected to the broker
        /// </summary>
        public override bool IsConnected => true;

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
            : this(apiKey, apiKeySecret, accessToken, isPaperTrading, algorithm?.Portfolio?.Transactions)
        { }

        /// <summary>
        /// Initializes a new instance of the <see cref="AlpacaBrokerage"/> class.
        /// </summary>
        /// <param name="apiKey">The API key for authentication with Alpaca.</param>
        /// <param name="apiKeySecret">The secret key for authentication with Alpaca.</param>
        /// <param name="isPaperTrading">Indicates whether the brokerage should use the paper trading environment.</param>
        /// <remarks>
        /// This constructor initializes a new instance of the <see cref="AlpacaBrokerage"/> class with the specified API key,
        /// API secret key, a flag indicating whether to use paper trading, and an instance of <see cref="IDataAggregator"/>.
        /// </remarks>
        public AlpacaBrokerage(string apiKey, string apiKeySecret, string accessToken, bool isPaperTrading, IOrderProvider orderProvider) : base("AlpacaBrokerage")
        {
            Initialize(apiKey, apiKeySecret, accessToken, isPaperTrading, orderProvider);
        }

        /// <summary>
        /// Initializes this instance
        /// </summary>
        private void Initialize(string apiKey, string apiKeySecret, string accessToken, bool isPaperTrading, IOrderProvider orderProvider)
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

            var environment = isPaperTrading ? Environments.Paper : Environments.Live;
            // trading api client
            _tradingClient = EnvironmentExtensions.GetAlpacaTradingClient(environment, tradingSecretKey ?? secretKey);
            // order updates
            _orderStreamingClient = EnvironmentExtensions.GetAlpacaStreamingClient(environment, tradingSecretKey ?? secretKey);
            _orderStreamingClient.OnTradeUpdate += HandleTradeUpdate;

            _symbolMapper = new AlpacaBrokerageSymbolMapper(_tradingClient);
            _orderProvider = orderProvider;

            if (secretKey != null)
            {
                // historical equity
                _equityHistoricalDataClient = EnvironmentExtensions.GetAlpacaDataClient(environment, secretKey);

                // historical options
                _optionsHistoricalDataClient = EnvironmentExtensions.GetAlpacaOptionsDataClient(environment, secretKey);

                // equity streaming client
                _equityStreamingClient = EnvironmentExtensions.GetAlpacaDataStreamingClient(environment, secretKey);

                // historical crypto
                _cryptoHistoricalDataClient = EnvironmentExtensions.GetAlpacaCryptoDataClient(environment, secretKey);
                // streaming crypto
                _cryptoStreamingClient = EnvironmentExtensions.GetAlpacaCryptoStreamingClient(environment, secretKey);

                foreach (var streamingClient in new IStreamingClient[] { _cryptoStreamingClient, _equityStreamingClient, _orderStreamingClient })
                {
                    streamingClient.Connected += (obj) => StreamingClient_Connected(streamingClient, obj);
                    streamingClient.OnWarning += (obj) => StreamingClient_OnWarning(streamingClient, obj);
                    streamingClient.SocketOpened += () => StreamingClient_SocketOpened(streamingClient); ;
                    streamingClient.SocketClosed += () => StreamingClient_SocketClosed(streamingClient);
                    streamingClient.OnError += (obj) => StreamingClient_OnError(streamingClient, obj);
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
        }

        private void StreamingClient_OnError(IStreamingClient client, Exception obj)
        {
            Log.Trace($"{nameof(StreamingClient_OnError)}({client.GetType().Name}): {obj}");
        }

        private void StreamingClient_SocketClosed(IStreamingClient client)
        {
            Log.Trace($"{nameof(StreamingClient_SocketClosed)}({client.GetType().Name}): SocketClosed");
        }

        private void StreamingClient_SocketOpened(IStreamingClient client)
        {
            Log.Trace($"{nameof(StreamingClient_SocketOpened)}({client.GetType().Name}): SocketOpened");
        }

        private void StreamingClient_OnWarning(IStreamingClient client, string obj)
        {
            Log.Trace($"{nameof(StreamingClient_OnWarning)}({client.GetType().Name}): {obj}");
        }

        private void StreamingClient_Connected(IStreamingClient client, AuthStatus obj)
        {
            Log.Trace($"{nameof(StreamingClient_Connected)}({client.GetType().Name}): {obj}");
        }

        #region Brokerage

        /// <summary>
        /// Gets all open orders on the account.
        /// NOTE: The order objects returned do not have QC order IDs.
        /// </summary>
        /// <returns>The open orders returned from IB</returns>
        public override List<Order> GetOpenOrders()
        {
            var orders = _tradingClient.ListOrdersAsync(new ListOrdersRequest() { OrderStatusFilter = OrderStatusFilter.Open }).SynchronouslyAwaitTaskResult();

            var leanOrders = new List<Order>();
            foreach (var brokerageOrder in orders)
            {
                var leanSymbol = _symbolMapper.GetLeanSymbol(brokerageOrder.AssetClass, brokerageOrder.Symbol);
                var quantity = (brokerageOrder.OrderSide == OrderSide.Buy ? brokerageOrder.Quantity : decimal.Negate(brokerageOrder.Quantity.Value)).Value;
                var leanOrder = default(Order);
                switch (brokerageOrder.OrderType)
                {
                    case AlpacaMarket.OrderType.Market:
                        leanOrder = new Orders.MarketOrder(leanSymbol, quantity, brokerageOrder.SubmittedAtUtc.Value);
                        break;
                    case AlpacaMarket.OrderType.Limit:
                        leanOrder = new Orders.LimitOrder(leanSymbol, quantity, brokerageOrder.LimitPrice.Value, brokerageOrder.SubmittedAtUtc.Value);
                        break;
                    case AlpacaMarket.OrderType.Stop:
                        leanOrder = new StopMarketOrder(leanSymbol, quantity, brokerageOrder.StopPrice.Value, brokerageOrder.SubmittedAtUtc.Value);
                        break;
                    case AlpacaMarket.OrderType.StopLimit:
                        leanOrder = new Orders.StopLimitOrder(leanSymbol, quantity, brokerageOrder.StopPrice.Value, brokerageOrder.LimitPrice.Value, brokerageOrder.SubmittedAtUtc.Value);
                        break;
                    case AlpacaMarket.OrderType.TrailingStop:
                        var trailingAsPercent = brokerageOrder.TrailOffsetInPercent.HasValue ? true : false;
                        var trailingAmount = brokerageOrder.TrailOffsetInPercent.HasValue ? brokerageOrder.TrailOffsetInPercent.Value / 100m : brokerageOrder.TrailOffsetInDollars.Value;
                        leanOrder = new Orders.TrailingStopOrder(leanSymbol, quantity, brokerageOrder.StopPrice.Value, trailingAmount, trailingAsPercent, brokerageOrder.SubmittedAtUtc.Value);
                        break;
                    default:
                        throw new NotSupportedException($"{nameof(AlpacaBrokerage)}.{nameof(GetOpenOrders)}: Order type '{brokerageOrder.OrderType}' is not supported.");
                }

                leanOrder.Status = Orders.OrderStatus.Submitted;
                if (brokerageOrder.FilledQuantity > 0 && brokerageOrder.FilledQuantity != brokerageOrder.Quantity)
                {
                    leanOrder.Status = Orders.OrderStatus.PartiallyFilled;
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

            var placeOrderResetEvent = new ManualResetEvent(false);
            try
            {
                lock (_lockObject)
                {
                    var response = _tradingClient.PostOrderAsync(orderRequest).SynchronouslyAwaitTaskResult();
                    order.BrokerId.Add(response.OrderId.ToString());
                    _resetEventByBrokerageOrderID[response.OrderId] = placeOrderResetEvent;
                }

                if (placeOrderResetEvent.WaitOne(TimeSpan.FromSeconds(10)))
                {
                    placeOrderResetEvent.Reset();
                    OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero, $"{nameof(AlpacaBrokerage)} Order Event")
                    {
                        Status = Orders.OrderStatus.Submitted
                    });
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero, $"{nameof(AlpacaBrokerage)}.{nameof(PlaceOrder)} Order Event")
                { Status = Orders.OrderStatus.Invalid, Message = ex.Message });
                return false;
            }
        }

        private void HandleTradeUpdate(ITradeUpdate obj)
        {
            Log.Debug($"{nameof(AlpacaBrokerage)}.{nameof(HandleTradeUpdate)}: {obj}");

            var leanOrderStatus = default(Orders.OrderStatus);
            switch (obj.Event)
            {
                case TradeEvent.PendingNew:
                    return;
                case TradeEvent.New:
                    if (_resetEventByBrokerageOrderID.TryRemove(obj.Order.OrderId, out var submittedResetEvent))
                    {
                        submittedResetEvent.Set();
                    }
                    return;
                case TradeEvent.Rejected:
                    if (_resetEventByBrokerageOrderID.TryRemove(obj.Order.OrderId, out var rejectedResetEvent))
                    {
                        rejectedResetEvent.Set();
                    }
                    break;
                case TradeEvent.Canceled:
                    if (_resetEventByBrokerageOrderID.TryRemove(obj.Order.OrderId, out var canceledResetEvent))
                    {
                        canceledResetEvent.Set();
                    }
                    return;
                case TradeEvent.Replaced:
                    if (_resetEventByBrokerageOrderID.TryRemove(obj.Order.ReplacedByOrderId.Value, out var updateResetEvent))
                    {
                        updateResetEvent.Set();
                    }
                    return;
                case TradeEvent.Fill:
                    leanOrderStatus = Orders.OrderStatus.Filled;
                    break;
                case TradeEvent.PartialFill:
                    leanOrderStatus = Orders.OrderStatus.PartiallyFilled;
                    break;
                default:
                    return;
            }

            var leanOrder = _orderProvider.GetOrdersByBrokerageId(obj.Order.OrderId.ToString())?.SingleOrDefault();
            if (leanOrder == null)
            {
                Log.Error($"{nameof(AlpacaBrokerage)}.{nameof(HandleTradeUpdate)}: order id not found: {obj.Order.OrderId}");
                return;
            }

            var leanSymbol = _symbolMapper.GetLeanSymbol(obj.Order.AssetClass, obj.Order.Symbol);

            var orderEvent = new OrderEvent(leanOrder, obj.TimestampUtc.HasValue ? obj.TimestampUtc.Value : obj.Order.SubmittedAtUtc.Value,
                new OrderFee(new CashAmount(0, Currencies.USD)))
            {
                Status = leanOrder.Status,
                FillPrice = obj.Price ?? 0m,
                FillQuantity = obj.Order.OrderSide == OrderSide.Buy ? obj.Order.FilledQuantity : Decimal.Negate(obj.Order.FilledQuantity),
            };

            OnOrderEvent(orderEvent);
        }

        /// <summary>
        /// Updates the order with the same id
        /// </summary>
        /// <param name="order">The new order information</param>
        /// <returns>True if the request was made for the order to be updated, false otherwise</returns>
        public override bool UpdateOrder(Order order)
        {
            var brokerageOrderId = order.BrokerId.Last();

            var pathOrderRequest = new ChangeOrderRequest(new Guid(brokerageOrderId)) { Quantity = Convert.ToInt64(order.AbsoluteQuantity) };

            switch (order)
            {
                case Orders.LimitOrder lo:
                    pathOrderRequest.LimitPrice = lo.LimitPrice;
                    break;
                case StopMarketOrder smo:
                    pathOrderRequest.StopPrice = smo.StopPrice;
                    break;
                case Orders.StopLimitOrder slo:
                    pathOrderRequest.LimitPrice = slo.LimitPrice;
                    pathOrderRequest.StopPrice = slo.StopPrice;
                    break;
            }

            var updateOrderResetEvent = new ManualResetEvent(false);
            try
            {
                lock (_lockObject)
                {
                    var response = _tradingClient.PatchOrderAsync(pathOrderRequest).SynchronouslyAwaitTaskResult();
                    order.BrokerId.Add(response.OrderId.ToString());
                    _resetEventByBrokerageOrderID[response.OrderId] = updateOrderResetEvent;
                }
            }
            catch (Exception ex)
            {
                OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero, $"{nameof(AlpacaBrokerage)}.{nameof(UpdateOrder)} Order Event")
                { Status = Orders.OrderStatus.Invalid, Message = ex.Message });
            }

            if (updateOrderResetEvent.WaitOne(TimeSpan.FromSeconds(10)))
            {
                updateOrderResetEvent.Reset();
                OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero, $"{nameof(AlpacaBrokerage)} Order Event")
                {
                    Status = Orders.OrderStatus.UpdateSubmitted
                });
                return true;
            }

            return false;
        }

        /// <summary>
        /// Cancels the order with the specified ID
        /// </summary>
        /// <param name="order">The order to cancel</param>
        /// <returns>True if the request was made for the order to be canceled, false otherwise</returns>
        public override bool CancelOrder(Order order)
        {
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

            var brokerageOrderId = new Guid(order.BrokerId.Last());
            var cancelOrderResetEvent = new ManualResetEvent(false);
            try
            {
                lock (_lockObject)
                {
                    var response = _tradingClient.CancelOrderAsync(brokerageOrderId).SynchronouslyAwaitTaskResult();
                    _resetEventByBrokerageOrderID[brokerageOrderId] = cancelOrderResetEvent;
                }

                if (cancelOrderResetEvent.WaitOne(TimeSpan.FromSeconds(10)))
                {
                    cancelOrderResetEvent.Reset();
                    OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero, $"{nameof(AlpacaBrokerage)} Order Event")
                    {
                        Status = Orders.OrderStatus.Canceled
                    });
                    return true;
                }

                return false;

            }
            catch (Exception ex)
            {
                OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero, $"{nameof(AlpacaBrokerage)}.{nameof(CancelOrder)} Order Event")
                { Status = Orders.OrderStatus.Invalid, Message = ex.Message });
                return false;
            }
        }

        /// <summary>
        /// Connects the client to the broker's remote servers
        /// </summary>
        public override void Connect()
        {
            foreach (var streamingClient in new IStreamingClient [] { _orderStreamingClient, _equityStreamingClient, _cryptoStreamingClient })
            {
                var authorizedStatus = streamingClient.ConnectAndAuthenticateAsync().SynchronouslyAwaitTaskResult();
                if (authorizedStatus != AuthStatus.Authorized)
                {
                    throw new InvalidOperationException($"Connect(): Failed to connect to {streamingClient.GetType().Name}");
                }
            }
        }

        /// <summary>
        /// Disconnects the client from the broker's remote servers
        /// </summary>
        public override void Disconnect()
        {
            _orderStreamingClient.DisconnectAsync().SynchronouslyAwaitTask();
            _equityStreamingClient.DisconnectAsync().SynchronouslyAwaitTask();
            _cryptoStreamingClient.DisconnectAsync().SynchronouslyAwaitTask();
        }

        public override void Dispose()
        {
            _tradingClient.DisposeSafely();

            _equityHistoricalDataClient.DisposeSafely();
            _cryptoHistoricalDataClient.DisposeSafely();
            _optionsHistoricalDataClient.DisposeSafely();

            // streaming
            _orderStreamingClient.DisposeSafely();
            _equityStreamingClient.DisposeSafely();
            _cryptoStreamingClient.DisposeSafely();
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
                var request = new RestRequest("modules/license/read", Method.POST) { RequestFormat = DataFormat.Json };
                request.AddParameter("application/json", JsonConvert.SerializeObject(information), ParameterType.RequestBody);
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
