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
    /// Indicates whether querying recent SIP (Securities Information Processor) data  
    /// is restricted due to subscription limitations.
    /// </summary>
    private bool _isSipDataRestricted;

    /// <summary>
    /// Indicates whether OPRA (Options Price Reporting Authority) data is restricted  
    /// due to the OPRA agreement not being signed, leading to a 15-minute delay.
    /// </summary>
    private bool _isOpraDataRestricted;

    /// <summary>
    /// Flag to ensure the warning message of <see cref="SecurityType.Equity"/> symbol for unsupported <see cref="TickType.Trade"/>
    /// <seealso cref="Resolution.Tick"/> and <seealso cref="Resolution.Second"/> is only logged once.
    /// </summary>
    private bool _unsupportedEquityTradeTickAndSecondResolution;

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
    /// Indicates whether a warning for an invalid start time has been logged, where the start time is greater than or equal to the end time in UTC.
    /// </summary>
    private volatile bool _invalidStartTimeWarningLogged;

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

        if (request.StartTimeUtc >= request.EndTimeUtc)
        {
            if (!_invalidStartTimeWarningLogged)
            {
                _invalidStartTimeWarningLogged = true;
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "InvalidStarTimeUtc",
                    "The history request's start time must be earlier than the end time. No data will be returned."));
            }
            return null;
        }

        IEnumerable<BaseData> data;
        switch (request.Symbol.SecurityType)
        {
            case SecurityType.Equity:
                data = GetEquityHistory(request, brokerageSymbol);
                break;
            case SecurityType.Option:
                data = GetOptionHistory(request, brokerageSymbol);
                break;
            case SecurityType.Crypto:
                data = GetCryptoHistory(request, brokerageSymbol);
                break;
            default:
                return null;
        }
        if (data != null)
        {
            return data.Where(x => request.ExchangeHours.IsOpen(x.Time, x.EndTime, request.IncludeExtendedMarketHours));
        }
        return data;
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
            // TODO: not supported by alpaca
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
        foreach (var response in CreatePaginationRequest(alpacaRequest, req => client.GetHistoricalQuotesAsync(req)))
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
        foreach (var response in CreatePaginationRequest(alpacaRequest, req => client.GetHistoricalTradesAsync(req)))
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
    /// Creates a pagination request for historical data based on the given request and callback function.
    /// </summary>
    /// <typeparam name="T">The type of the historical request (must implement IHistoricalRequest).</typeparam>
    /// <typeparam name="U">The type of the data being returned in the paginated response.</typeparam>
    /// <param name="request">The request object that contains the pagination parameters.</param>
    /// <param name="callback">A callback function that performs the API request and returns a paginated result.</param>
    /// <param name="paginationSize">The size of each page of results. Defaults to 10,000.</param>
    /// <returns>An enumerable of paginated responses, yielding one page at a time.</returns>
    private IEnumerable<IMultiPage<U>> CreatePaginationRequest<T, U>(T request, Func<T, Task<IMultiPage<U>>> callback, uint paginationSize = 10_000)
    where T : IHistoricalRequest
    {
        var response = default(IMultiPage<U>);
        var repeatByRestrictException = default(bool);
        do
        {
            repeatByRestrictException = false;
            // If the token is null, it indicates the first request; otherwise, it's a subsequent page.
            if ((_isSipDataRestricted || _isOpraDataRestricted) && !IsCryptoRequest(request) && string.IsNullOrEmpty(request.Pagination.Token))
            {
                try
                {
                    request = (T)ChangeIntoTimeIntervalInHistoricalRequest(request);
                }
                catch (ArgumentException)
                {
                    break;
                }
            }

            try
            {
                request.Pagination.Size ??= paginationSize;
                response = callback(request).SynchronouslyAwaitTaskResult();
            }
            catch (RestClientErrorException ex) when (HandleHistoricalFreeRestrictionException(ex.Message, out var messageType, out var messageText))
            {
                repeatByRestrictException = true;
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, messageType, messageText));
                continue;
            }

            if (response.Items.Count == 0)
            {
                continue;
            }

            yield return response;
            request.Pagination.Token = response.NextPageToken;
        } while (!string.IsNullOrEmpty(request.Pagination.Token) || repeatByRestrictException);
    }

    /// <summary>
    /// Handles specific SIP restriction exceptions and returns appropriate warning messages.
    /// </summary>
    private bool HandleHistoricalFreeRestrictionException(string message, out string messageType, out string messageText)
    {
        switch (message.ToLowerInvariant())
        {
            case "opra agreement is not signed":
                _isOpraDataRestricted = true;
                messageType = "OPRADataRestriction";
                messageText = "OPRA agreement is not signed for free subscriptions. Historical data will have a 15-minute delay.";
                return true;

            case "subscription does not permit querying recent sip data":
                _isSipDataRestricted = true;
                messageType = "SIPDataRestriction";
                messageText = "Real-time SIP data is restricted for free subscriptions. Historical data will have a 15-minute delay.";
                return true;

            default:
                messageType = null;
                messageText = null;
                return false;
        }
    }

    /// <summary>
    /// Determines whether the given historical request is related to cryptocurrency data.
    /// </summary>
    /// <param name="historicalRequest">The historical request object.</param>
    /// <returns><c>true</c> if the request is for cryptocurrency data; otherwise, <c>false</c>.</returns>
    private bool IsCryptoRequest(IHistoricalRequest historicalRequest) =>
        historicalRequest is HistoricalCryptoBarsRequest or HistoricalCryptoQuotesRequest or HistoricalCryptoTradesRequest;

    /// <summary>
    /// Adjusts the time interval in a historical request to account for restricted SIP data access.
    /// </summary>
    /// <param name="historicalRequest">The original historical request object.</param>
    /// <returns>A new historical request with the adjusted time range.</returns>
    /// <exception cref="NotImplementedException">Thrown if the request type is not handled.</exception>
    /// <exception cref="ArgumentException">Thrown if the adjusted time range is invalid.</exception>
    private IHistoricalRequest ChangeIntoTimeIntervalInHistoricalRequest(IHistoricalRequest historicalRequest)
    {
        var end = DateTime.UtcNow.AddMinutes(-15);
        var start = (historicalRequest as HistoricalRequestBase).TimeInterval.From.Value;

        if (start >= end)
        {
            throw new ArgumentException($"{nameof(AlpacaBrokerage)}.{nameof(ChangeIntoTimeIntervalInHistoricalRequest)}: Invalid time range: SIP's 15-minute delay may cause end time to fall before start, especially across trading days. Adjust accordingly.");
        }

        return historicalRequest switch
        {
            HistoricalBarsRequest hbr when _isSipDataRestricted => new HistoricalBarsRequest(hbr.Symbols, start, end, hbr.TimeFrame),
            HistoricalQuotesRequest hqr when _isSipDataRestricted => new HistoricalQuotesRequest(hqr.Symbols, start, end),
            HistoricalOptionTradesRequest hor when _isOpraDataRestricted => new HistoricalOptionTradesRequest(hor.Symbols, start, end),
            HistoricalOptionBarsRequest hobr when _isOpraDataRestricted => new HistoricalOptionBarsRequest(hobr.Symbols, start, end, hobr.TimeFrame),
            _ when _isOpraDataRestricted || _isSipDataRestricted => historicalRequest,
            _ => throw new NotImplementedException($"{nameof(AlpacaBrokerage)}.{nameof(ChangeIntoTimeIntervalInHistoricalRequest)}: The historical request type '{historicalRequest.GetType().FullName}' is not implemented."),
        };
    }
}
