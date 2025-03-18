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

using Moq;
using System;
using NodaTime;
using System.Linq;
using NUnit.Framework;
using QuantConnect.Data;
using QuantConnect.Util;
using QuantConnect.Tests;
using QuantConnect.Interfaces;
using QuantConnect.Securities;
using System.Collections.Generic;

namespace QuantConnect.Brokerages.Alpaca.Tests
{
    [TestFixture]
    public class AlpacaBrokerageHistoryProviderTests
    {
        private AlpacaBrokerage _alpacaBrokerage;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var (apiKey, apiKeySecret, isPaperTrading, accessToken) = AlpacaBrokerageTestHelpers.GetConfigParameters();
            _alpacaBrokerage = new AlpacaBrokerage(apiKey, apiKeySecret, accessToken, isPaperTrading, new Mock<IAlgorithm>().Object);
        }

        private static IEnumerable<TestCaseData> TestParameters
        {
            get
            {
                yield return new TestCaseData(Symbols.AAPL, Resolution.Minute, TickType.Trade, new DateTime(2024, 6, 12, 9, 30, 0), new DateTime(2024, 6, 17, 16, 0, 0));
                yield return new TestCaseData(Symbols.AAPL, Resolution.Hour, TickType.Trade, new DateTime(2024, 6, 17, 9, 30, 0), new DateTime(2024, 6, 17, 16, 0, 0));
                yield return new TestCaseData(Symbols.AAPL, Resolution.Daily, TickType.Trade, new DateTime(2024, 6, 17, 9, 30, 0), new DateTime(2024, 6, 21, 16, 0, 0));

                yield return new TestCaseData(Symbols.AAPL, Resolution.Tick, TickType.Quote, new DateTime(2024, 6, 17, 9, 30, 0), new DateTime(2024, 6, 17, 16, 0, 0));
                yield return new TestCaseData(Symbols.AAPL, Resolution.Second, TickType.Quote, new DateTime(2024, 6, 17, 9, 30, 0), new DateTime(2024, 6, 17, 16, 0, 0));
                yield return new TestCaseData(Symbols.AAPL, Resolution.Minute, TickType.Quote, new DateTime(2024, 6, 17, 9, 30, 0), new DateTime(2024, 6, 17, 16, 0, 0));
                yield return new TestCaseData(Symbols.AAPL, Resolution.Hour, TickType.Quote, new DateTime(2024, 6, 17, 9, 30, 0), new DateTime(2024, 6, 17, 16, 0, 0));
                yield return new TestCaseData(Symbols.AAPL, Resolution.Daily, TickType.Quote, new DateTime(2024, 6, 17, 9, 30, 0), new DateTime(2024, 6, 17, 16, 0, 0));

                var AAPLOption = Symbol.CreateOption(Symbols.AAPL, Symbols.AAPL.ID.Market, OptionStyle.American, OptionRight.Call, 100, new DateTime(2024, 06, 21));
                yield return new TestCaseData(AAPLOption, Resolution.Tick, TickType.Trade, new DateTime(2024, 6, 12, 9, 30, 0), new DateTime(2024, 6, 21, 16, 0, 0));
                yield return new TestCaseData(AAPLOption, Resolution.Second, TickType.Trade, new DateTime(2024, 6, 12, 9, 30, 0), new DateTime(2024, 6, 21, 16, 0, 0));
                yield return new TestCaseData(AAPLOption, Resolution.Minute, TickType.Trade, new DateTime(2024, 6, 12, 9, 30, 0), new DateTime(2024, 6, 21, 16, 0, 0));
                yield return new TestCaseData(AAPLOption, Resolution.Hour, TickType.Trade, new DateTime(2024, 6, 12, 9, 30, 0), new DateTime(2024, 6, 21, 16, 0, 0));
                yield return new TestCaseData(AAPLOption, Resolution.Daily, TickType.Trade, new DateTime(2024, 6, 12, 9, 30, 0), new DateTime(2024, 6, 21, 16, 0, 0));

                yield return new TestCaseData(Symbols.BTCUSD, Resolution.Tick, TickType.Trade, new DateTime(2024, 6, 12, 9, 30, 0), new DateTime(2024, 6, 17, 16, 0, 0));
                yield return new TestCaseData(Symbols.BTCUSD, Resolution.Second, TickType.Trade, new DateTime(2024, 6, 12, 9, 30, 0), new DateTime(2024, 6, 17, 16, 0, 0));
                yield return new TestCaseData(Symbols.BTCUSD, Resolution.Minute, TickType.Trade, new DateTime(2024, 6, 12, 9, 30, 0), new DateTime(2024, 6, 17, 16, 0, 0));
                yield return new TestCaseData(Symbols.BTCUSD, Resolution.Hour, TickType.Trade, new DateTime(2024, 6, 17, 9, 30, 0), new DateTime(2024, 6, 17, 16, 0, 0));
                yield return new TestCaseData(Symbols.BTCUSD, Resolution.Daily, TickType.Trade, new DateTime(2024, 6, 17, 9, 30, 0), new DateTime(2024, 6, 21, 16, 0, 0));

                yield return new TestCaseData(Symbols.BTCUSD, Resolution.Tick, TickType.Quote, new DateTime(2024, 6, 17, 9, 30, 0), new DateTime(2024, 6, 17, 16, 0, 0));
                yield return new TestCaseData(Symbols.BTCUSD, Resolution.Second, TickType.Quote, new DateTime(2024, 6, 17, 9, 30, 0), new DateTime(2024, 6, 17, 16, 0, 0));
                yield return new TestCaseData(Symbols.BTCUSD, Resolution.Minute, TickType.Quote, new DateTime(2024, 6, 17, 9, 30, 0), new DateTime(2024, 6, 17, 16, 0, 0));
                yield return new TestCaseData(Symbols.BTCUSD, Resolution.Hour, TickType.Quote, new DateTime(2024, 6, 17, 9, 30, 0), new DateTime(2024, 6, 21, 16, 0, 0));
                yield return new TestCaseData(Symbols.BTCUSD, Resolution.Daily, TickType.Quote, new DateTime(2024, 6, 17, 9, 30, 0), new DateTime(2024, 6, 21, 16, 0, 0));
            }
        }

        private static IEnumerable<TestCaseData> NotSupportHistoryParameters
        {
            get
            {
                yield return new TestCaseData(Symbols.AAPL, Resolution.Tick, TickType.Trade, new DateTime(default), new DateTime(default));
                yield return new TestCaseData(Symbols.AAPL, Resolution.Second, TickType.Trade, new DateTime(default), new DateTime(default));

                yield return new TestCaseData(Symbols.AAPL, Resolution.Second, TickType.OpenInterest, new DateTime(default), new DateTime(default));
                yield return new TestCaseData(Symbols.AAPL, Resolution.Minute, TickType.OpenInterest, new DateTime(default), new DateTime(default));
                yield return new TestCaseData(Symbols.AAPL, Resolution.Hour, TickType.OpenInterest, new DateTime(default), new DateTime(default));
                yield return new TestCaseData(Symbols.AAPL, Resolution.Daily, TickType.OpenInterest, new DateTime(default), new DateTime(default));

                yield return new TestCaseData(Symbols.AAPL, Resolution.Tick, TickType.OpenInterest, new DateTime(2024, 6, 10, 9, 30, 0), new DateTime(2024, 6, 17, 16, 0, 0));

                var AAPLOption = Symbol.CreateOption(Symbols.AAPL, Symbols.AAPL.ID.Market, OptionStyle.American, OptionRight.Call, 100, new DateTime(2024, 06, 21));
                yield return new TestCaseData(AAPLOption, Resolution.Second, TickType.OpenInterest, new DateTime(default), new DateTime(default));
                yield return new TestCaseData(AAPLOption, Resolution.Minute, TickType.OpenInterest, new DateTime(default), new DateTime(default));
                yield return new TestCaseData(AAPLOption, Resolution.Hour, TickType.OpenInterest, new DateTime(default), new DateTime(default));
                yield return new TestCaseData(AAPLOption, Resolution.Daily, TickType.OpenInterest, new DateTime(default), new DateTime(default));

                yield return new TestCaseData(AAPLOption, Resolution.Tick, TickType.Quote, new DateTime(2024, 6, 17, 9, 30, 0), new DateTime(2024, 6, 17, 16, 0, 0));
                yield return new TestCaseData(AAPLOption, Resolution.Second, TickType.Quote, new DateTime(2024, 6, 17, 9, 30, 0), new DateTime(2024, 6, 17, 16, 0, 0));
                yield return new TestCaseData(AAPLOption, Resolution.Minute, TickType.Quote, new DateTime(2024, 6, 17, 9, 30, 0), new DateTime(2024, 6, 17, 16, 0, 0));
                yield return new TestCaseData(AAPLOption, Resolution.Hour, TickType.Quote, new DateTime(2024, 6, 17, 9, 30, 0), new DateTime(2024, 6, 17, 16, 0, 0));
                yield return new TestCaseData(AAPLOption, Resolution.Daily, TickType.Quote, new DateTime(2024, 6, 17, 9, 30, 0), new DateTime(2024, 6, 17, 16, 0, 0));

                yield return new TestCaseData(Symbols.BTCUSD, Resolution.Daily, TickType.OpenInterest, new DateTime(default), new DateTime(default));
            }
        }

        [Test, TestCaseSource(nameof(NotSupportHistoryParameters))]
        public void GetsHistoryWithNotSupportedParameters(Symbol symbol, Resolution resolution, TickType tickType, DateTime startDate, DateTime endDate)
        {
            var historyRequest = CreateHistoryRequest(symbol, resolution, tickType, startDate, endDate);
            var histories = _alpacaBrokerage.GetHistory(historyRequest)?.ToList();
            Assert.IsNull(histories);
        }

        [Test, TestCaseSource(nameof(TestParameters))]
        public void GetsHistory(Symbol symbol, Resolution resolution, TickType tickType, DateTime startDate, DateTime endDate)
        {
            var historyRequest = CreateHistoryRequest(symbol, resolution, tickType, startDate, endDate);

            var histories = _alpacaBrokerage.GetHistory(historyRequest).ToList();
            Assert.Greater(histories.Count, 0);
            Assert.IsTrue(histories.All(x => x.EndTime - x.Time == resolution.ToTimeSpan()));
            Assert.IsTrue(histories.All(x => x.Symbol == symbol));
            Assert.IsTrue(histories.All(x => x is not Data.Market.Tick tick || tick.TickType == tickType));
        }

        [TestCase(SecurityType.Equity, Resolution.Tick, TickType.Quote)]
        [TestCase(SecurityType.Equity, Resolution.Daily, TickType.Quote)]
        [TestCase(SecurityType.Equity, Resolution.Second, TickType.Quote)]
        [TestCase(SecurityType.Option, Resolution.Hour, TickType.Trade)]
        [TestCase(SecurityType.Option, Resolution.Second, TickType.Trade)]
        [TestCase(SecurityType.Option, Resolution.Daily, TickType.Trade)]
        [TestCase(SecurityType.Equity, Resolution.Daily, TickType.Trade)]
        [TestCase(SecurityType.Crypto, Resolution.Daily, TickType.Trade)]
        public void ValidateMaxAvailableHistoricalDataInFreeSubscription(SecurityType securityType, Resolution resolution, TickType tickType)
        {
            var symbol = securityType switch
            {
                SecurityType.Equity => Symbols.AAPL,
                SecurityType.Crypto => Symbols.BTCUSD,
                SecurityType.Option => Symbol.CreateOption(Symbols.AAPL, Market.USA, SecurityType.Option.DefaultOptionStyle(), OptionRight.Call, 215m, new DateTime(2025, 03, 21)),
                _ => throw new NotImplementedException("")
            };
            var utcNow = DateTime.UtcNow;
            var startDate = utcNow.AddDays(-1);
            var endDate = utcNow;

            var historyRequest = CreateHistoryRequest(symbol, resolution, tickType, startDate, endDate);

            Logging.Log.Trace($"[ValidateMaxAvailableHistoricalDataInFreeSubscription] Symbol: {symbol}, Resolution: {resolution}, TickType: {tickType}, " +
                  $"UtcNow: {utcNow:O}, StartDate: {startDate:O}, EndDate: {endDate:O}");

            var histories = _alpacaBrokerage.GetHistory(historyRequest).ToList();
            Assert.Greater(histories.Count, 0);

            var resolutionInTimeSpan = resolution.ToTimeSpan();
            Assert.IsTrue(histories.All(x => x.EndTime - x.Time == resolutionInTimeSpan));

            if (resolution < Resolution.Hour)
            {
                var lastBaseDataTimeUtc = histories.Last().Time.ConvertToUtc(TimeZones.NewYork);
                var utcMinusSipAllowed = utcNow.AddMinutes(-15);
                var differenceBetweenLastBaseDataAndUtcRequested = Math.Abs((lastBaseDataTimeUtc - utcMinusSipAllowed).TotalSeconds);

                switch (securityType)
                {
                    case SecurityType.Equity:
                        Assert.IsTrue(differenceBetweenLastBaseDataAndUtcRequested < 1);
                        break;
                    case SecurityType.Option: // less volatility, acceptable value 30 seconds
                        Assert.IsTrue(differenceBetweenLastBaseDataAndUtcRequested <= 30,
                            $"Difference Between Last Base Data and Utc Requested: {differenceBetweenLastBaseDataAndUtcRequested} seconds");
                        break;
                }
            }
        }

        [TestCase(SecurityType.Equity, Resolution.Tick, TickType.Quote)]
        [TestCase(SecurityType.Equity, Resolution.Second, TickType.Quote)]
        [TestCase(SecurityType.Option, Resolution.Second, TickType.Trade)]
        public void ValidateLast10minutesAvailableHistoricalDataInFreeSubscription(SecurityType securityType, Resolution resolution, TickType tickType)
        {
            var symbol = securityType switch
            {
                SecurityType.Equity => Symbols.AAPL,
                SecurityType.Option => Symbol.CreateOption(Symbols.AAPL, Market.USA, SecurityType.Option.DefaultOptionStyle(), OptionRight.Call, 212.5m, new DateTime(2025, 03, 21)),
                _ => throw new NotImplementedException("")
            };

            var utcNow = DateTime.UtcNow;
            var startDate = utcNow.AddMinutes(-10);
            var endDate = utcNow;

            var historyRequest = CreateHistoryRequest(symbol, resolution, tickType, startDate, endDate);

            Logging.Log.Trace($"[ValidateMaxAvailableHistoricalDataInFreeSubscription] Symbol: {symbol}, Resolution: {resolution}, TickType: {tickType}, " +
                  $"UtcNow: {utcNow:O}, StartDate: {startDate:O}, EndDate: {endDate:O}");

            var histories = _alpacaBrokerage.GetHistory(historyRequest).ToList();
            Assert.AreEqual(histories.Count, 0);
        }

        [Test]
        public void ValidateAmountBrokerageMessagesInHistoricalDataRequests()
        {
            var actualExceptionMessages = new List<string>();
            _alpacaBrokerage.Message += (object _, BrokerageMessageEvent messageEvent) =>
            {
                actualExceptionMessages.Add(messageEvent.Message);
            };

            var AAPL = Symbols.AAPL;
            var AAPL_Option = Symbol.CreateOption(Symbols.AAPL, Market.USA, SecurityType.Option.DefaultOptionStyle(), OptionRight.Call, 212.5m, new DateTime(2025, 03, 21));

            var utcNow = DateTime.UtcNow;
            var startDate = utcNow.AddMinutes(-20);
            var endDate = utcNow;

            var historyRequests = new[]
            {
                CreateHistoryRequest(AAPL, Resolution.Second, TickType.Quote, startDate, endDate),
                CreateHistoryRequest(AAPL_Option, Resolution.Second, TickType.Trade, startDate, endDate)
            };

            var historyRequestCounter = default(int);
            foreach (var historyRequest in historyRequests.Concat(historyRequests))
            {
                var histories = _alpacaBrokerage.GetHistory(historyRequest).ToList();
                Assert.Greater(histories.Count, 0);
                historyRequestCounter++;
            }

            Assert.AreEqual(4, historyRequestCounter);
            Assert.AreEqual(2, actualExceptionMessages.Count);
            Assert.That(actualExceptionMessages, Has.Some.Matches<string>(msg =>
                msg.Contains("OPRA agreement is not signed for free subscriptions.")
                || msg.Contains("Real-time SIP data is restricted for free subscriptions.")));
        }

        internal static HistoryRequest CreateHistoryRequest(Symbol symbol, Resolution resolution, TickType tickType, DateTime startDateTime,
            DateTime endDateTime, SecurityExchangeHours exchangeHours = null, DateTimeZone dataTimeZone = null)
        {
            if (exchangeHours == null)
            {
                exchangeHours = SecurityExchangeHours.AlwaysOpen(TimeZones.NewYork);
            }

            if (dataTimeZone == null)
            {
                dataTimeZone = TimeZones.NewYork;
            }

            var dataType = LeanData.GetDataType(resolution, tickType);
            return new HistoryRequest(
                startTimeUtc: startDateTime,
                endTimeUtc: endDateTime,
                dataType: dataType,
                symbol: symbol,
                resolution: resolution,
                exchangeHours: exchangeHours,
                dataTimeZone: dataTimeZone,
                fillForwardResolution: null,
                includeExtendedMarketHours: true,
                isCustomData: false,
                dataNormalizationMode: DataNormalizationMode.Adjusted,
                tickType: tickType
                );
        }
    }
}
