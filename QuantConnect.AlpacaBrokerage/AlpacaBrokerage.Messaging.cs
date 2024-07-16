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

using Alpaca.Markets;
using QuantConnect.Logging;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.Concurrent;

namespace QuantConnect.Brokerages.Alpaca;

public partial class AlpacaBrokerage
{
    private ConcurrentDictionary<Symbol, IAlpacaDataSubscription[]> _alpacaDataSubscriptionByLeanSymbol = new();

    /// <summary>
    /// Adds the specified symbols to the subscription
    /// </summary>
    /// <param name="symbols">The symbols to be added keyed by SecurityType</param>
    private bool Subscribe(IEnumerable<Symbol> symbols)
    {
        foreach (var symbol in symbols)
        {
            var res = AlpacaTradingClient.GetClockAsync().SynchronouslyAwaitTaskResult();
            var brokerageSymbol = _symbolMapper.GetBrokerageSymbol(symbol);
            var tradeSubscription = default(IAlpacaDataSubscription<ITrade>);
            var quoteSubscription = default(IAlpacaDataSubscription<IQuote>);
            if (symbol.SecurityType == SecurityType.Crypto)
            {
                tradeSubscription = AlpacaCryptoStreamingClient.GetTradeSubscription(brokerageSymbol);
                tradeSubscription.Received += HandleTradeReceived;
                tradeSubscription.OnSubscribedChanged += TradeSubscription_OnSubscribedChanged;

                quoteSubscription = AlpacaCryptoStreamingClient.GetQuoteSubscription(brokerageSymbol);
                quoteSubscription.Received += HandleQuoteReceived;


                Task.Run(async () => await AlpacaCryptoStreamingClient.SubscribeAsync(tradeSubscription));
                Task.Run(async () => await AlpacaCryptoStreamingClient.SubscribeAsync(quoteSubscription));
            }
            else
            {
                tradeSubscription = AlpacaDataStreamingClient.GetTradeSubscription();
                tradeSubscription.Received += HandleTradeReceived;
                tradeSubscription.OnSubscribedChanged += TradeSubscription_OnSubscribedChanged;

                quoteSubscription = AlpacaDataStreamingClient.GetQuoteSubscription();
                quoteSubscription.Received += HandleQuoteReceived;

                AlpacaDataStreamingClient.SubscribeAsync(tradeSubscription).ConfigureAwait(false).GetAwaiter().GetResult();
                AlpacaDataStreamingClient.SubscribeAsync(quoteSubscription).ConfigureAwait(false).GetAwaiter().GetResult();
            }
            _alpacaDataSubscriptionByLeanSymbol[symbol] = new IAlpacaDataSubscription[] { tradeSubscription, quoteSubscription };
        }

        return true;
    }

    private void TradeSubscription_OnSubscribedChanged()
    {
        Log.Debug($"{nameof(TradeSubscription_OnSubscribedChanged)}: WTF");
    }

    /// <summary>
    /// Removes the specified symbols to the subscription
    /// </summary>
    /// <param name="symbols">The symbols to be removed keyed by SecurityType</param>
    private bool Unsubscribe(IEnumerable<Symbol> symbols)
    {
        foreach (var symbol in symbols)
        {
            if (_alpacaDataSubscriptionByLeanSymbol.TryRemove(symbol, out var subscriptions))
            {
                if (symbol.SecurityType == SecurityType.Crypto)
                {
                    foreach (var subscription in subscriptions)
                    {
                        AlpacaCryptoStreamingClient.UnsubscribeAsync(subscription).AsTask().ConfigureAwait(continueOnCapturedContext: false).GetAwaiter().GetResult();
                    }
                }
                else
                {
                    foreach (var subscription in subscriptions)
                    {
                        AlpacaDataStreamingClient.UnsubscribeAsync(subscription).AsTask().ConfigureAwait(continueOnCapturedContext: false).GetAwaiter().GetResult();
                    }
                }
            }
        }
        return true;
    }

    private void HandleTradeReceived(ITrade obj)
    {
        Log.Debug($"{nameof(HandleTradeReceived)}: {obj}");
    }
    private void HandleQuoteReceived(IQuote obj)
    {
        Log.Debug($"{nameof(HandleQuoteReceived)}: {obj}");
    }
}
