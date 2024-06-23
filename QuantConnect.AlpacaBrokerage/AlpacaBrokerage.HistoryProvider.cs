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
                throw new NotSupportedException($"{nameof(AlpacaBrokerage)}.{nameof(GetHistory)}: SecurityType '{request.Symbol.SecurityType}' is not supported.");
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
                return GetCryptoHistoricalTickTradeBar(request.Symbol, brokerageSymbol, request.StartTimeUtc, request.EndTimeUtc);
            case TickType.Trade when request.Resolution == Resolution.Second:
                return LeanData.AggregateTicksToTradeBars(GetCryptoHistoricalTickTradeBar(request.Symbol, brokerageSymbol, request.StartTimeUtc, request.EndTimeUtc), request.Symbol, request.Resolution.ToTimeSpan());
            case TickType.Trade:
                return GetCryptoHistoricalTradeBar(request.Symbol, brokerageSymbol, request.StartTimeUtc, request.EndTimeUtc,
                    request.Resolution.ConvertLeanResolutionToAlpacaBarTimeFrame(), request.Resolution.ToTimeSpan());
            case TickType.Quote when request.Resolution == Resolution.Tick:
                return GetCryptoHistoricalTickQuoteBar(request.Symbol, brokerageSymbol, request.StartTimeUtc, request.EndTimeUtc);
            case TickType.Quote:
                return LeanData.AggregateTicks(
                    GetCryptoHistoricalTickQuoteBar(request.Symbol, brokerageSymbol, request.StartTimeUtc, request.EndTimeUtc),
                    request.Symbol,
                    request.Resolution.ToTimeSpan());
            default:
                throw new NotSupportedException($"{nameof(AlpacaBrokerage)}.{nameof(GetOptionHistory)}: The TickType '{request.TickType}' is not supported for option data.");
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
                return GetOptionHistoricalTickTradeBar(request.Symbol, brokerageSymbol, request.StartTimeUtc, request.EndTimeUtc);
            case TickType.Trade when request.Resolution == Resolution.Second:
                return LeanData.AggregateTicksToTradeBars(
                    GetOptionHistoricalTickTradeBar(request.Symbol, brokerageSymbol, request.StartTimeUtc, request.EndTimeUtc),
                    request.Symbol,
                    request.Resolution.ToTimeSpan());
            case TickType.Trade:
                return GetOptionHistoricalTradeBar(request.Symbol, brokerageSymbol, request.StartTimeUtc, request.EndTimeUtc,
                    request.Resolution.ConvertLeanResolutionToAlpacaBarTimeFrame(), request.Resolution.ToTimeSpan());
            default:
                throw new NotSupportedException($"{nameof(AlpacaBrokerage)}.{nameof(GetOptionHistory)}: The TickType '{request.TickType}' is not supported for option data.");
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

        if (request.TickType == TickType.OpenInterest && request.Resolution != Resolution.Tick)
        {
            if (!_unsupportedOpenInterestNonTickResolution)
            {
                _unsupportedOpenInterestNonTickResolution = true;
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "InvalidResolution",
                    $"The requested resolution '{request.Resolution}' is not supported for open interest data. Only tick resolution is supported."));
            }
            return null;
        }

        switch (request.TickType)
        {
            case TickType.Trade:
                return GetEquityHistoricalTradeBar(request.Symbol, brokerageSymbol,
                    request.Resolution.ConvertLeanResolutionToAlpacaBarTimeFrame(), request.StartTimeUtc, request.EndTimeUtc, request.Resolution.ToTimeSpan());
            case TickType.Quote:
                if (request.Resolution == Resolution.Tick)
                {
                    return GetEquityHistoricalTickQuoteBar(request.Symbol, brokerageSymbol, request.StartTimeUtc, request.EndTimeUtc);
                }
                return LeanData.AggregateTicks(
                    GetEquityHistoricalTickQuoteBar(request.Symbol, brokerageSymbol, request.StartTimeUtc, request.EndTimeUtc),
                    request.Symbol,
                    request.Resolution.ToTimeSpan());
            case TickType.OpenInterest:
                return GetEquityHistoricalAuction(request.Symbol, brokerageSymbol, request.StartTimeUtc, request.EndTimeUtc);
            default:
                throw new NotSupportedException($"{nameof(AlpacaBrokerage)}.{nameof(GetEquityHistory)}: The TickType '{request.TickType}' is not supported. Please provide a valid TickType.");
        }
    }

    /// <summary>
    /// Retrieves historical auction data for an <see cref="SecurityType.Equity"/> symbol within a specified date range.
    /// </summary>
    /// <param name="leanSymbol">The internal Lean symbol representation.</param>
    /// <param name="brokerageSymbol">The brokerage-specific symbol representation.</param>
    /// <param name="startDate">The start date for the historical data request.</param>
    /// <param name="endDate">The end date for the historical data request.</param>
    /// <returns>An enumerable collection of <see cref="OpenInterest"/> objects representing the historical auction data.</returns>
    private IEnumerable<OpenInterest> GetEquityHistoricalAuction(Symbol leanSymbol, string brokerageSymbol, DateTime startDate, DateTime endDate)
    {
        var historyAuctionRequest = new HistoricalAuctionsRequest(brokerageSymbol, startDate, endDate);
        
        foreach (var response in CreatePaginationRequest(historyAuctionRequest, req => AlpacaDataClient.GetHistoricalAuctionsAsync(req)))
        {
            foreach (var auction in response.Items[brokerageSymbol])
            {
                foreach (var opening in auction.Openings)
                {
                    yield return new OpenInterest(opening.TimestampUtc, leanSymbol, opening.Price);
                }
            }
        }
    }

    /// <summary>
    /// Retrieves historical tick quote bars for an <see cref="SecurityType.Equity"/> symbol within a specified date range.
    /// </summary>
    /// <param name="leanSymbol">The internal Lean symbol representation.</param>
    /// <param name="brokerageSymbol">The brokerage-specific symbol representation.</param>
    /// <param name="startDate">The start date for the historical data request.</param>
    /// <param name="endDate">The end date for the historical data request.</param>
    /// <returns>An enumerable collection of <see cref="Tick"/> objects representing the historical quote data.</returns>
    private IEnumerable<Tick> GetEquityHistoricalTickQuoteBar(Symbol leanSymbol, string brokerageSymbol, DateTime startDate, DateTime endDate)
    {
        var historyQuoteRequest = new HistoricalQuotesRequest(brokerageSymbol, startDate, endDate) { Feed = _marketDataFeed };

        foreach (var response in CreatePaginationRequest(historyQuoteRequest, req => AlpacaDataClient.GetHistoricalQuotesAsync(req)))
        {
            foreach (var quote in response.Items[brokerageSymbol])
            {
                // If the array contains one flag, it applies to both the bid and ask.
                // If the array contains two flags, the first one applies to the bid and the second one to the ask.
                var (bidCondition, askCondition) = quote.Conditions.Count > 1 ? (quote.Conditions[0], quote.Conditions[1]) : (quote.Conditions[0], quote.Conditions[0]);
                // TODO: The brokerage returns 2 conditions and 2 exchanges, but Lean currently does not handle this scenario.
                yield return new Tick(quote.TimestampUtc, leanSymbol, bidCondition, quote.AskExchange, quote.BidSize, quote.BidPrice, quote.AskSize, quote.AskPrice);
            }
        }
    }

    /// <summary>
    /// Retrieves historical trade bars for an <see cref="SecurityType.Equity"/> symbol within a specified date range and timeframe.
    /// </summary>
    /// <param name="leanSymbol">The internal Lean symbol representation.</param>
    /// <param name="brokerageSymbol">The brokerage-specific symbol representation.</param>
    /// <param name="barTimeFrame">The timeframe for each bar (e.g., minute, hour, day).</param>
    /// <param name="startDate">The start date for the historical data request.</param>
    /// <param name="endDate">The end date for the historical data request.</param>
    /// <param name="period">The time span representing the duration of each trade bar.</param>
    /// <returns>An enumerable collection of <see cref="TradeBar"/> objects representing the historical data.</returns>
    private IEnumerable<TradeBar> GetEquityHistoricalTradeBar(Symbol leanSymbol, string brokerageSymbol,
        BarTimeFrame barTimeFrame, DateTime startDate, DateTime endDate, TimeSpan period)
    {
        var historyTradeRequest = new HistoricalBarsRequest(brokerageSymbol, startDate, endDate, barTimeFrame){ Feed = _marketDataFeed };

        foreach (var response in CreatePaginationRequest(historyTradeRequest, req => AlpacaDataClient.GetHistoricalBarsAsync(req)))
        {
            foreach (var trade in response.Items[brokerageSymbol])
            {
                yield return new TradeBar(trade.TimeUtc, leanSymbol, trade.Open, trade.High, trade.Low, trade.Close, trade.Volume, period);
            }
        }
    }

    /// <summary>
    /// Retrieves historical tick trade bars for <see cref="SecurityType.Option"/> based on the specified parameters.
    /// </summary>
    /// <param name="leanSymbol">The internal Lean symbol representation for the option.</param>
    /// <param name="brokerageSymbol">The brokerage-specific symbol representation for the option.</param>
    /// <param name="startDate">The start date for the historical data request.</param>
    /// <param name="endDate">The end date for the historical data request.</param>
    /// <returns>An enumerable collection of <see cref="Tick"/> objects representing the historical tick trade bar data for options.</returns>
    private IEnumerable<Tick> GetOptionHistoricalTickTradeBar(Symbol leanSymbol, string brokerageSymbol, DateTime startDate, DateTime endDate)
    {
        var historyOptionRequest = new HistoricalOptionTradesRequest(brokerageSymbol, startDate, endDate);

        foreach (var response in CreatePaginationRequest(historyOptionRequest, req => AlpacaOptionsDataClient.GetHistoricalTradesAsync(historyOptionRequest)))
        {
            foreach (var trade in response.Items[brokerageSymbol])
            {
                // If the array contains one flag, it applies to both the bid and ask.
                // If the array contains two flags, the first one applies to the bid and the second one to the ask.
                var (bidCondition, askCondition) = trade.Conditions.Count > 1 ? (trade.Conditions[0], trade.Conditions[1]) : (trade.Conditions[0], trade.Conditions[0]);
                // TODO: The brokerage returns 2 conditions and 2 exchanges, but Lean currently does not handle this scenario.
                yield return new Tick(trade.TimestampUtc, leanSymbol, bidCondition, trade.Exchange, trade.Size, trade.Price);
            }
        }
    }

    /// <summary>
    /// Retrieves historical trade bars for <see cref="SecurityType.Option"/> based on the specified parameters.
    /// </summary>
    /// <param name="leanSymbol">The internal Lean symbol representation.</param>
    /// <param name="brokerageSymbol">The brokerage-specific symbol representation for options.</param>
    /// <param name="startDate">The start date for the historical data request.</param>
    /// <param name="endDate">The end date for the historical data request.</param>
    /// <param name="barTimeFrame">The timeframe for each bar (e.g., minute, hour, day).</param>
    /// <param name="period">The time span representing the duration of each trade bar.</param>
    /// <returns>An enumerable collection of <see cref="TradeBar"/> objects representing the historical trade bar data for options.</returns>
    private IEnumerable<TradeBar> GetOptionHistoricalTradeBar(Symbol leanSymbol, string brokerageSymbol, DateTime startDate, DateTime endDate,
    BarTimeFrame barTimeFrame, TimeSpan period)
    {
        var historyOptionRequest = new HistoricalOptionBarsRequest(brokerageSymbol, startDate, endDate, barTimeFrame);

        foreach (var response in CreatePaginationRequest(historyOptionRequest, req => AlpacaOptionsDataClient.GetHistoricalBarsAsync(historyOptionRequest)))
        {
            foreach (var trade in response.Items[brokerageSymbol])
            {
                yield return new TradeBar(trade.TimeUtc, leanSymbol, trade.Open, trade.High, trade.Low, trade.Close, trade.Volume, period);
            }
        }
    }

    /// <summary>
    /// Retrieves historical tick quote bars for <see cref="SecurityType.Crypto"/> symbol based on the specified parameters.
    /// </summary>
    /// <param name="leanSymbol">The internal Lean symbol representation for the cryptocurrency.</param>
    /// <param name="brokerageSymbol">The brokerage-specific symbol representation for the cryptocurrency.</param>
    /// <param name="startDate">The start date for the historical data request.</param>
    /// <param name="endDate">The end date for the historical data request.</param>
    /// <returns>An enumerable collection of <see cref="Tick"/> objects representing the historical tick quote bar data for cryptocurrency.</returns>
    private IEnumerable<Tick> GetCryptoHistoricalTickQuoteBar(Symbol leanSymbol, string brokerageSymbol, DateTime startDate, DateTime endDate)
    {
        var historyCryptoRequest = new HistoricalCryptoQuotesRequest(brokerageSymbol, startDate, endDate);

        foreach (var response in CreatePaginationRequest(historyCryptoRequest, req => AlpacaCryptoDataClient.GetHistoricalQuotesAsync(historyCryptoRequest)))
        {
            foreach (var quote in response.Items[brokerageSymbol])
            {
                yield return new Tick(quote.TimestampUtc, leanSymbol, string.Empty, quote.AskExchange, quote.BidSize, quote.BidPrice, quote.AskSize, quote.AskPrice);
            }
        }
    }

    /// <summary>
    /// Retrieves historical tick trade bars for <see cref="SecurityType.Crypto"/> symbol based on the specified parameters.
    /// </summary>
    /// <param name="leanSymbol">The internal Lean symbol representation for the cryptocurrency.</param>
    /// <param name="brokerageSymbol">The brokerage-specific symbol representation for the cryptocurrency.</param>
    /// <param name="startDate">The start date for the historical data request.</param>
    /// <param name="endDate">The end date for the historical data request.</param>
    /// <returns>An enumerable collection of <see cref="Tick"/> objects representing the historical tick trade bar data for cryptocurrency.</returns>
    private IEnumerable<Tick> GetCryptoHistoricalTickTradeBar(Symbol leanSymbol, string brokerageSymbol, DateTime startDate, DateTime endDate)
    {
        var historyCryptoRequest = new HistoricalCryptoTradesRequest(brokerageSymbol, startDate, endDate);

        foreach (var response in CreatePaginationRequest(historyCryptoRequest, req => AlpacaCryptoDataClient.GetHistoricalTradesAsync(historyCryptoRequest)))
        {
            foreach (var trade in response.Items[brokerageSymbol])
            {
                yield return new Tick(trade.TimestampUtc, leanSymbol, string.Empty, trade.Exchange, trade.Size, trade.Price);
            }
        }
    }

    /// <summary>
    /// Retrieves historical trade bars for <see cref="SecurityType.Crypto"/> symbol based on the specified parameters.
    /// </summary>
    /// <param name="leanSymbol">The internal Lean symbol representation for the cryptocurrency.</param>
    /// <param name="brokerageSymbol">The brokerage-specific symbol representation for the cryptocurrency.</param>
    /// <param name="startDate">The start date for the historical data request.</param>
    /// <param name="endDate">The end date for the historical data request.</param>
    /// <param name="barTimeFrame">The timeframe for each bar (e.g., minute, hour, day).</param>
    /// <param name="period">The time span representing the duration of each trade bar.</param>
    /// <returns>An enumerable collection of <see cref="TradeBar"/> objects representing the historical trade bar data for cryptocurrency.</returns>
    private IEnumerable<TradeBar> GetCryptoHistoricalTradeBar(Symbol leanSymbol, string brokerageSymbol,
        DateTime startDate, DateTime endDate, BarTimeFrame barTimeFrame, TimeSpan period)
    {
        var historyCryptoRequest = new HistoricalCryptoBarsRequest(brokerageSymbol, startDate, endDate, barTimeFrame);

        foreach (var response in CreatePaginationRequest(historyCryptoRequest, req => AlpacaCryptoDataClient.GetHistoricalBarsAsync(historyCryptoRequest)))
        {
            foreach (var trade in response.Items[brokerageSymbol])
            {
                yield return new TradeBar(trade.TimeUtc, leanSymbol, trade.Open, trade.High, trade.Low, trade.Close, trade.Volume, period);
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
