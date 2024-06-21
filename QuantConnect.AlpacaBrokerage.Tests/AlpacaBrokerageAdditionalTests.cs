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
using NUnit.Framework;
using QuantConnect.Util;
using QuantConnect.Tests;
using QuantConnect.Interfaces;
using System.Collections.Generic;

namespace QuantConnect.Brokerages.Alpaca.Tests
{
    [TestFixture]
    public class AlpacaBrokerageAdditionalTests
    {
        /// <inheritdoc cref="AlpacaBrokerage"/>
        private AlpacaBrokerage _alpacaBrokerage;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var (apiKey, apiKeySecret, dataFeedProvider, isPaperTrading) = AlpacaBrokerageTestHelpers.GetConfigParameters();

            var algorithmMock = new Mock<IAlgorithm>();
            _alpacaBrokerage = new AlpacaBrokerage(apiKey, apiKeySecret, dataFeedProvider, isPaperTrading, algorithmMock.Object);
        }

        [Test]
        public void ParameterlessConstructorComposerUsage()
        {
            var brokerage = Composer.Instance.GetExportedValueByTypeName<IDataQueueHandler>("AlpacaBrokerage");
            Assert.IsNotNull(brokerage);
        }

        private static IEnumerable<Symbol> QuoteSymbolParameters
        {
            get
            {
                TestGlobals.Initialize();
                yield return Symbols.AAPL;
                yield return Symbols.BTCUSD;
                yield return Symbol.CreateOption(Symbols.AAPL, Symbols.AAPL.ID.Market, OptionStyle.American, OptionRight.Call, 5, new DateTime(2024, 06, 21));
            }
        }

        [Test, TestCaseSource(nameof(QuoteSymbolParameters))]
        public void GetLatestQuote(Symbol symbol)
        {
            var quote = _alpacaBrokerage.GetLatestQuote(symbol);

            Assert.IsNotNull(quote);
            Assert.Greater(quote.AskSize, 0);
            Assert.Greater(quote.AskPrice, 0);
            Assert.Greater(quote.BidSize, 0);
            Assert.Greater(quote.BidPrice, 0);
        }

        [TestCase("iex")]
        [TestCase("IEX")]
        [TestCase("Iex")]
        [TestCase("sip")]
        [TestCase("SIP")]
        [TestCase("Sip")]
        [TestCase("otc")]
        public void CreateAlpacaBrokerageWithDifferentDataProviders(string configDataProvider)
        {
            var (apiKey, apiKeySecret, dataFeedProvider, isPaperTrading) = AlpacaBrokerageTestHelpers.GetConfigParameters(false);

            dataFeedProvider = configDataProvider;

            var alpacaBrokerage = new AlpacaBrokerage(apiKey, apiKeySecret, dataFeedProvider, isPaperTrading, new Mock<IAlgorithm>().Object);

            Assert.IsNotNull(alpacaBrokerage);
        }
    }
}