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
using QuantConnect.Util;
using QuantConnect.Data;
using System.Threading.Tasks;
using QuantConnect.Interfaces;
using QuantConnect.Data.Market;
using System.Collections.Generic;
using AlpacaMarket = Alpaca.Markets;

namespace QuantConnect.Brokerages.Alpaca;

public partial class AlpacaBrokerage
{
    /// <summary>
    /// Flag to ensure the warning message of <see cref="SecurityType.Equity"/> symbol for unsupported <see cref="TickType.Trade"/>
    /// <seealso cref="Resolution.Tick"/> and <seealso cref="Resolution.Second"/> is only logged once.
    /// </summary>
    private bool _unsupportedEquityTradeTickAndSecondResolution;

    /// <summary>
    /// Flag to ensure the warning message for unsupported <see cref="TickType.OpenInterest"/> resolutions
    /// other than <seealso cref="Resolution.Tick"/> is only logged once.
    /// </summary>
    private bool _unsupportedOpenInterestNonTickResolution;

    /// <summary>
    /// Flag to ensure the warning message for unsupported <see cref="SecurityType.Option"/> <seealso cref="TickType"/> is only logged once.
    /// </summary>
    private bool _unsupportedOptionTickType;

    /// <summary>
    /// Flag to ensure the warning message of <see cref="SecurityType.Crypto"/> symbol for unsupported tick type is only logged once.
    /// </summary>
    private bool _unsupportedCryptoTickType;

    /// <summary>
    /// Indicates whether a warning message for unsupported <see cref="SecurityType"/> types has been logged.
    /// </summary>
    private bool _unsupportedSecurityTypeWarningLogged;

    /// <summary>
    /// Gets the history for the requested symbols
    /// <see cref="IBrokerage.GetHistory(HistoryRequest)"/>
    /// </summary>
    /// <param name="request">The historical data request</param>
    /// <returns>An enumerable of bars covering the span specified in the request</returns>
    public override IEnumerable<BaseData> GetHistory(HistoryRequest request)
    {
        if (!CanSubscribe(request.Symbol))
        {
            if (!_unsupportedSecurityTypeWarningLogged)
            {
                _unsupportedSecurityTypeWarningLogged = true;
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "UnsupportedSecurityType",
                $"The security type '{request.Symbol.SecurityType}' of symbol '{request.Symbol}' is not supported for historical data retrieval."));
            }
            return null;
        }

        var brokerageSymbol = _symbolMapper.GetBrokerageSymbol(request.Symbol);

        switch (request.Symbol.SecurityType)
        {
            case SecurityType.Equity:
                return GetEquityHistory(request, brokerageSymbol);
            case SecurityType.Option:
                return GetOptionHistory(request, brokerageSymbol);
            case SecurityType.Crypto:
                return GetCryptoHistory(request, brokerageSymbol);
            default:
                return null;
        }
    }

    /// <summary>
    /// Retrieves historical data for <see cref="SecurityType.Crypto"/> symbol based on the specified history request and brokerage symbol.
    /// </summary>
    /// <param name="request">The history request containing the parameters for the data retrieval.</param>
    /// <param name="brokerageSymbol">The brokerage-specific symbol representation for cryptocurrency.</param>
    /// <returns>An enumerable collection of <see cref="BaseData"/> objects representing the historical data for cryptocurrency.</returns>
    /// <exception cref="NotSupportedException">Thrown when an unsupported <see cref="TickType"/> is encountered.</exception>
    private IEnumerable<BaseData> GetCryptoHistory(HistoryRequest request, string brokerageSymbol)
    {
        if (request.TickType == TickType.OpenInterest)
        {
            if (!_unsupportedCryptoTickType)
            {
                _unsupportedCryptoTickType = true;
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "InvalidTickType",
                    $"The requested TickType '{request.TickType}' is not supported for {SecurityType.Crypto} data."));
            }
            return null;
        }

        switch (request.TickType)
        {
            case TickType.Trade when request.Resolution == Resolution.Tick:
                return GetGenericHistoricalTradeTick(request, brokerageSymbol, _cryptoHistoricalDataClient, new HistoricalCryptoTradesRequest(brokerageSymbol, request.StartTimeUtc, request.EndTimeUtc));
            case TickType.Trade when request.Resolution == Resolution.Second:
                var data = GetGenericHistoricalTradeTick(request, brokerageSymbol, _cryptoHistoricalDataClient, new HistoricalCryptoTradesRequest(brokerageSymbol, request.StartTimeUtc, request.EndTimeUtc));
                return LeanData.AggregateTicksToTradeBars(data, request.Symbol, request.Resolution.ToTimeSpan());
            case TickType.Trade:
                var alpacaRequest = new HistoricalCryptoBarsRequest(brokerageSymbol, request.StartTimeUtc, request.EndTimeUtc, request.Resolution.ConvertLeanResolutionToAlpacaBarTimeFrame());
                return GetGenericHistoricalTradeBar(request, brokerageSymbol, _cryptoHistoricalDataClient, alpacaRequest);

            case TickType.Quote:
                var quoteTicks = GetGenericHistoricalQuoteTick(request, brokerageSymbol, _cryptoHistoricalDataClient, new HistoricalCryptoQuotesRequest(brokerageSymbol, request.StartTimeUtc, request.EndTimeUtc));
                if (request.Resolution == Resolution.Tick)
                {
                    return quoteTicks;
                }
                return LeanData.AggregateTicks(quoteTicks, request.Symbol, request.Resolution.ToTimeSpan());
            default:
                return null;
        }
    }

    /// <summary>
    /// Retrieves historical data for <see cref="SecurityType.Option"/> based on the specified history request and brokerage symbol.
    /// </summary>
    /// <param name="request">The history request containing the parameters for the data retrieval.</param>
    /// <param name="brokerageSymbol">The brokerage-specific symbol representation for options.</param>
    /// <returns>An enumerable collection of <see cref="BaseData"/> objects representing the historical data for options.</returns>
    /// <exception cref="NotSupportedException">Thrown when an unsupported <see cref="TickType"/> or resolution is encountered.</exception>
    private IEnumerable<BaseData> GetOptionHistory(HistoryRequest request, string brokerageSymbol)
    {
        if (request.TickType != TickType.Trade)
        {
            if (!_unsupportedOptionTickType)
            {
                _unsupportedOptionTickType = true;
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "InvalidTickType",
                    $"The requested TickType '{request.TickType}' is not supported for option data. Only ${TickType.Trade} type is supported."));
            }
            return null;
        }

        switch (request.TickType)
        {
            case TickType.Trade when request.Resolution == Resolution.Tick:
                return GetGenericHistoricalTradeTick(request, brokerageSymbol, _optionsHistoricalDataClient, new HistoricalOptionTradesRequest(brokerageSymbol, request.StartTimeUtc, request.EndTimeUtc));
            case TickType.Trade when request.Resolution == Resolution.Second:
                var data = GetGenericHistoricalTradeTick(request, brokerageSymbol, _optionsHistoricalDataClient, new HistoricalOptionTradesRequest(brokerageSymbol, request.StartTimeUtc, request.EndTimeUtc));
                return LeanData.AggregateTicksToTradeBars(data, request.Symbol, request.Resolution.ToTimeSpan());
            case TickType.Trade:
                var alpacaRequest = new HistoricalOptionBarsRequest(brokerageSymbol, request.StartTimeUtc, request.EndTimeUtc, request.Resolution.ConvertLeanResolutionToAlpacaBarTimeFrame());
                return GetGenericHistoricalTradeBar(request, brokerageSymbol, _optionsHistoricalDataClient, alpacaRequest);
            case TickType.OpenInterest:
            // TODO
            default:
                return null;
        }
    }

    /// <summary>
    /// Retrieves historical data for an equity symbol based on the specified history request and brokerage symbol.
    /// </summary>
    /// <param name="request">The history request containing the parameters for the data retrieval.</param>
    /// <param name="brokerageSymbol">The brokerage-specific symbol representation.</param>
    /// <returns>An enumerable collection of <see cref="BaseData"/> objects representing the historical data.</returns>
    /// <exception cref="NotSupportedException">Thrown when an unsupported <see cref="TickType"/> is encountered.</exception>
    private IEnumerable<BaseData> GetEquityHistory(HistoryRequest request, string brokerageSymbol)
    {
        if (request.TickType == TickType.Trade && request.Resolution is Resolution.Second or Resolution.Tick)
        {
            if (!_unsupportedEquityTradeTickAndSecondResolution)
            {
                _unsupportedEquityTradeTickAndSecondResolution = true;
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "InvalidResolution",
                    $"The requested resolution '{request.Resolution}' is not supported for trade tick data. No historical data will be returned."));
            }
            return null;
        }

        switch (request.TickType)
        {
            case TickType.Trade:
                var alpacaRequest = new HistoricalBarsRequest(brokerageSymbol, request.StartTimeUtc, request.EndTimeUtc, request.Resolution.ConvertLeanResolutionToAlpacaBarTimeFrame());
                return GetGenericHistoricalTradeBar(request, brokerageSymbol, _equityHistoricalDataClient, alpacaRequest);
            case TickType.Quote:
                var data = GetGenericHistoricalQuoteTick(request, brokerageSymbol, _equityHistoricalDataClient, new HistoricalQuotesRequest(brokerageSymbol, request.StartTimeUtc, request.EndTimeUtc));
                if (request.Resolution == Resolution.Tick)
                {
                    return data;
                }
                return LeanData.AggregateTicks(data, request.Symbol, request.Resolution.ToTimeSpan());
            default:
                return null;
        }
    }

    private IEnumerable<Tick> GetGenericHistoricalQuoteTick<T>(HistoryRequest leanRequest, string brokerageSymbol, IHistoricalQuotesClient<T> client, T alpacaRequest)
        where T : IHistoricalRequest<T, IQuote>
    {
        foreach (var response in CreatePaginationRequest(alpacaRequest, req => client.GetHistoricalQuotesAsync(alpacaRequest)))
        {
            foreach (var quote in response.Items[brokerageSymbol])
            {
                var condition = string.Empty;
                if (quote.Conditions.Count > 1)
                {
                    condition = quote.Conditions[0];
                }
                var tick = new Tick(quote.TimestampUtc.ConvertFromUtc(leanRequest.ExchangeHours.TimeZone), leanRequest.Symbol, condition, quote.AskExchange, quote.BidSize, quote.BidPrice, quote.AskSize, quote.AskPrice);
                yield return tick;
            }
        }
    }

    private IEnumerable<Tick> GetGenericHistoricalTradeTick<T>(HistoryRequest leanRequest, string brokerageSymbol, IHistoricalTradesClient<T> client, T alpacaRequest)
        where T : IHistoricalRequest<T, ITrade>
    {
        foreach (var response in CreatePaginationRequest(alpacaRequest, req => client.GetHistoricalTradesAsync(alpacaRequest)))
        {
            foreach (var trade in response.Items[brokerageSymbol])
            {
                var condition = string.Empty;
                if (trade.Conditions.Count > 1)
                {
                    condition = trade.Conditions[0];
                }
                var tick = new Tick(trade.TimestampUtc.ConvertFromUtc(leanRequest.ExchangeHours.TimeZone), leanRequest.Symbol, condition, trade.Exchange, trade.Size, trade.Price);
                yield return tick;
            }
        }
    }

    private IEnumerable<TradeBar> GetGenericHistoricalTradeBar<T>(HistoryRequest leanRequest, string brokerageSymbol, IHistoricalBarsClient<T> client, T alpacaRequest)
        where T : IHistoricalRequest<T, AlpacaMarket.IBar>
    {
        var period = leanRequest.Resolution.ToTimeSpan();
        foreach (var response in CreatePaginationRequest(alpacaRequest, req => client.GetHistoricalBarsAsync(req)))
        {
            foreach (var trade in response.Items[brokerageSymbol])
            {
                var bar = new TradeBar(trade.TimeUtc.ConvertFromUtc(leanRequest.ExchangeHours.TimeZone), leanRequest.Symbol, trade.Open, trade.High, trade.Low, trade.Close, trade.Volume, period);
                yield return bar;
            }
        }
    }

    /// <summary>
    /// Creates a pagination request for a given request object, using a callback function
    /// to asynchronously fetch paginated results until all pages are retrieved.
    /// </summary>
    /// <typeparam name="T">The type of request object that implements <see cref="IHistoricalRequest"/>.</typeparam>
    /// <typeparam name="U">The type of elements contained in each page of the response implementing <see cref="IMultiPage{U}"/>.</typeparam>
    /// <param name="request">The request object specifying the pagination parameters.</param>
    /// <param name="callback">The asynchronous function callback that fetches a page of results.</param>
    /// <param name="paginationSize">The maximum number of items per page (default is 10,000).</param>
    /// <returns>An enumerable sequence of paginated responses, each implementing <see cref="IMultiPage{U}"/>.</returns>
    /// <remarks>
    /// This method iterates over pages of results until there are no more pages to retrieve,
    /// using the <paramref name="callback"/> function to fetch each page asynchronously.
    /// It assumes that the <paramref name="request"/> object has pagination settings configured,
    /// and it updates the <paramref name="request"/> object with the next page token after each retrieval.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="request"/> or <paramref name="callback"/> is null.</exception>
    private IEnumerable<IMultiPage<U>> CreatePaginationRequest<T, U>(T request, Func<T, Task<IMultiPage<U>>> callback, uint paginationSize = 10_000)
        where T : IHistoricalRequest
    {
        request.Pagination.Size = paginationSize;
        do
        {
            var response = callback(request).SynchronouslyAwaitTaskResult();
            if (response.Items.Count == 0)
            {
                continue;
            }

            yield return response;
            request.Pagination.Token = response.NextPageToken;
        } while (!string.IsNullOrEmpty(request.Pagination.Token));
    }
}
