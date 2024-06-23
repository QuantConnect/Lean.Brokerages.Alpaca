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
    /// Flag to ensure the warning message for unsupported <see cref="TickType.Trade"/>
    /// <seealso cref="Resolution.Tick"/> and <seealso cref="Resolution.Second"/> is only logged once.
    /// </summary>
    private bool _unsupportedTradeTickAndSecondResolution;

    /// <summary>
    /// Flag to ensure the warning message for unsupported <see cref="TickType.OpenInterest"/> resolutions
    /// other than <seealso cref="Resolution.Tick"/> is only logged once.
    /// </summary>
    private bool _unsupportedOpenInterestNonTickResolution;

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
            return null;
        }

        if (request.TickType == TickType.Trade && request.Resolution is Resolution.Second or Resolution.Tick)
        {
            if (!_unsupportedTradeTickAndSecondResolution)
            {
                _unsupportedTradeTickAndSecondResolution = true;
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

        var brokerageSymbol = _symbolMapper.GetBrokerageSymbol(request.Symbol);

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
                return null;
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
            yield return response;
            request.Pagination.Token = response.NextPageToken;
        } while (!string.IsNullOrEmpty(request.Pagination.Token));
    }
}
