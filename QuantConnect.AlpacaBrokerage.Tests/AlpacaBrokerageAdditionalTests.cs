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
using Alpaca.Markets;
using NUnit.Framework;
using QuantConnect.Util;
using QuantConnect.Tests;
using QuantConnect.Interfaces;
using System.Collections.Generic;
using QuantConnect.Configuration;

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
            var apiKey = Config.Get("alpaca-api-key-id");
            var apiKeySecret = Config.Get("alpaca-api-secret-key");
            var isPaperTrading = Config.GetBool("alpaca-use-paper-trading");

            if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(apiKeySecret))
            {
                throw new ArgumentNullException("API Key or Secret Key cannot be null or empty. Please check your configuration.");
            }

            var algorithmMock = new Mock<IAlgorithm>();
            var secretKey = new SecretKey(apiKey, apiKeySecret);
            _alpacaBrokerage = new AlpacaBrokerage(apiKey, apiKeySecret, isPaperTrading, algorithmMock.Object);
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
    }
}