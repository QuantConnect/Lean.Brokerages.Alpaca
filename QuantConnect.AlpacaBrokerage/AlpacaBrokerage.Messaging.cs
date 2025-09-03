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
using NodaTime;
using Alpaca.Markets;
using QuantConnect.Logging;
using QuantConnect.Securities;
using QuantConnect.Data.Market;
using System.Collections.Generic;
using System.Collections.Concurrent;

namespace QuantConnect.Brokerages.Alpaca;

public partial class AlpacaBrokerage
{
    private readonly ConcurrentDictionary<string, SymbolSubscriptionData> _dataSubscriptionByBrokerageSymbol = new();
    private readonly ConcurrentDictionary<Symbol, IAlpacaDataSubscription[]> _dataSubscriptionByLeanSymbol = new();

    /// <summary>
    /// Adds the specified symbols to the subscription
    /// </summary>
    /// <param name="symbols">The symbols to be added keyed by SecurityType</param>
    private bool Subscribe(IEnumerable<Symbol> symbols)
    {
        foreach (var symbol in symbols)
        {
            var brokerageSymbol = _symbolMapper.GetBrokerageSymbol(symbol);

            var exchangeTimeZone = MarketHoursDatabase.FromDataFolder().GetExchangeHours(symbol.ID.Market, symbol, symbol.SecurityType).TimeZone;
            _dataSubscriptionByBrokerageSymbol[brokerageSymbol] = new() { Symbol = symbol, ExchangeTimeZone = exchangeTimeZone };

            var streamingClient = GetStreamingDataClient(symbol.SecurityType);
            var tradeSubscription = streamingClient.GetTradeSubscription(brokerageSymbol);
            tradeSubscription.Received += HandleTradeReceived;

            var quoteSubscription = streamingClient.GetQuoteSubscription(brokerageSymbol);
            quoteSubscription.Received += HandleQuoteReceived;

            streamingClient.SubscribeAsync(tradeSubscription).ConfigureAwait(false).GetAwaiter().GetResult();
            streamingClient.SubscribeAsync(quoteSubscription).ConfigureAwait(false).GetAwaiter().GetResult();

            _dataSubscriptionByLeanSymbol[symbol] = new IAlpacaDataSubscription[] { tradeSubscription, quoteSubscription };
        }
        return true;
    }

    /// <summary>
    /// Removes the specified symbols to the subscription
    /// </summary>
    /// <param name="symbols">The symbols to be removed keyed by SecurityType</param>
    private bool Unsubscribe(IEnumerable<Symbol> symbols)
    {
        foreach (var symbol in symbols)
        {
            if (_dataSubscriptionByLeanSymbol.TryRemove(symbol, out var subscriptions))
            {
                var streamingClient = GetStreamingDataClient(symbol.SecurityType);
                foreach (var subscription in subscriptions)
                {
                    if (subscription is IAlpacaDataSubscription<IQuote> quoteSubscription)
                    {
                        quoteSubscription.Received -= HandleQuoteReceived;
                    }
                    else if(subscription is IAlpacaDataSubscription<ITrade> tradeSubscription)
                    {
                        tradeSubscription.Received -= HandleTradeReceived;
                    }
                    streamingClient.UnsubscribeAsync(subscription).ConfigureAwait(false).GetAwaiter().GetResult();
                }
                _dataSubscriptionByBrokerageSymbol.TryRemove(_symbolMapper.GetBrokerageSymbol(symbol), out _);
            }
        }
        return true;
    }

    /// <summary>
    /// Returns the streaming client wrapper for the specified <see cref="SecurityType"/>.
    /// </summary>
    /// <param name="securityType">The type of security (e.g., Equity, Option, Crypto).</param>
    /// <returns>An <see cref="AlpacaStreamingClientWrapper"/> corresponding to the security type.</returns>
    /// <exception cref="NotSupportedException">Thrown if the security type is not recognized.</exception>
    private IStreamingDataClient GetStreamingDataClient(SecurityType securityType)
    {
        var streamingClient = securityType switch
        {
            SecurityType.Crypto => _cryptoStreamingClient,
            SecurityType.Equity => _equityStreamingClient,
            SecurityType.Option => _optionsStreamingClient,
            _ => throw new NotSupportedException($"{nameof(AlpacaBrokerage)}.{nameof(GetStreamingDataClient)}: Security type '{securityType}' is not supported.")
        };

        ConnectAndAuthenticate(streamingClient);

        return streamingClient.StreamingClient;
    }

    private void HandleTradeReceived(ITrade obj)
    {
        if (Log.DebuggingEnabled)
        {
            Log.Debug($"{nameof(HandleTradeReceived)}.JsonRealTimeTrade: Symbol={obj.Symbol}, Price={obj.Price}, Quantity={obj.Size}");
        }

        if (!_dataSubscriptionByBrokerageSymbol.TryGetValue(obj.Symbol, out var subscriptionData))
        {
            return;
        }
        var tick = new Tick()
        {
            Value = obj.Price,
            Quantity = obj.Size,

            TickType = TickType.Trade,
            Symbol = subscriptionData.Symbol,
            Time = DateTime.UtcNow.ConvertFromUtc(subscriptionData.ExchangeTimeZone),
        };
        lock (_aggregator)
        {
            _aggregator.Update(tick);
        }
    }

    private void HandleQuoteReceived(IQuote obj)
    {
        if (Log.DebuggingEnabled)
        {
            Log.Debug($"{nameof(HandleQuoteReceived)}.JsonRealTimeQuote: Symbol={obj.Symbol} [AskPrice={obj.AskPrice}, AskSize={obj.AskSize}] [BidPrice={obj.BidPrice}, BidSize={obj.BidSize}]");
        }

        if (!_dataSubscriptionByBrokerageSymbol.TryGetValue(obj.Symbol, out var subscriptionData))
        {
            return;
        }

        var tick = new Tick
        {
            AskSize = obj.AskSize,
            AskPrice = obj.AskPrice,

            BidSize = obj.BidSize,
            BidPrice = obj.BidPrice,

            TickType = TickType.Quote,
            Symbol = subscriptionData.Symbol,
            Time = DateTime.UtcNow.ConvertFromUtc(subscriptionData.ExchangeTimeZone),
        };

        lock (_aggregator)
        {
            _aggregator.Update(tick);
        }
    }

    private class SymbolSubscriptionData
    {
        public Symbol Symbol { get; internal set; }
        public DateTimeZone ExchangeTimeZone { get; internal set; }
    }
}
