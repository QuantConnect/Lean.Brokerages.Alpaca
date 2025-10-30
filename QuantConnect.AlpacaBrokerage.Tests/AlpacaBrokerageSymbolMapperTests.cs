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
using NUnit.Framework;

namespace QuantConnect.Brokerages.Alpaca.Tests
{
    [TestFixture]
    public class AlpacaBrokerageSymbolMapperTests
    {
        /// <inheritdoc cref="AlpacaBrokerageSymbolMapper"/>
        private AlpacaBrokerageSymbolMapper _symbolMapper;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var (apiKey, apiKeySecret, _, _) = AlpacaBrokerageTestHelpers.GetConfigParameters();

            var secretKey = new SecretKey(apiKey, apiKeySecret);
            var alpacaTradingClient = Environments.Paper.GetAlpacaTradingClient(secretKey);
            _symbolMapper = new(alpacaTradingClient);
        }

        [TestCase(AssetClass.UsOption, "AAPL240614C00100000", "AAPL", "2024/06/14", OptionRight.Call, 100)]
        [TestCase(AssetClass.UsOption, "AAPL240614P00100000", "AAPL", "2024/06/14", OptionRight.Put, 100)]
        [TestCase(AssetClass.UsOption, "AAPL240614C00235000", "AAPL", "2024/06/14", OptionRight.Call, 235)]
        [TestCase(AssetClass.UsOption, "QQQ240613C00484000", "QQQ", "2024/06/13", OptionRight.Call, 484)]
        [TestCase(AssetClass.UsOption, "QQQ240613C00484000", "QQQ", "2024/06/13", OptionRight.Call, 484)]
        public void ReturnsCorrectLeanSymbol(AssetClass brokerageAssetClass, string brokerageTicker, string expectedSymbol, DateTime expectedDateTime, OptionRight optionRight, decimal expectedStrike)
        {
            var leanSymbol = _symbolMapper.GetLeanSymbol(brokerageAssetClass, brokerageTicker);

            Assert.IsNotNull(leanSymbol);
            Assert.That(leanSymbol.ID.Date, Is.EqualTo(expectedDateTime));
            Assert.That(leanSymbol.ID.OptionRight, Is.EqualTo(optionRight));
            Assert.That(leanSymbol.ID.StrikePrice, Is.EqualTo(expectedStrike));
            Assert.That(leanSymbol.ID.Symbol, Is.EqualTo(expectedSymbol));
        }

        [Test]
        public void CallGetLeanSymbolWithNullAssetClassShouldThrowException()
        {
            Assert.Throws<NotSupportedException>(() => _symbolMapper.GetLeanSymbol(null, "AAPL"));
        }

        [TestCase("AAPL", SecurityType.Equity, null, null, null, "AAPL")]
        [TestCase("INTL", SecurityType.Equity, null, null, null, "INTL")]
        [TestCase("AAPL", SecurityType.Option, OptionRight.Call, 100, "2024/06/14", "AAPL240614C00100000")]
        [TestCase("AAPL", SecurityType.Option, OptionRight.Call, 105, "2024/06/14", "AAPL240614C00105000")]
        [TestCase("AAPL", SecurityType.Option, OptionRight.Put, 265, "2024/06/14", "AAPL240614P00265000")]
        [TestCase("BTCUSDT", SecurityType.Crypto, null, null, null, "BTC/USDT")]
        [TestCase("ETHUSD", SecurityType.Crypto, null, null, null, "ETH/USD")]
        public void ReturnsCorrectBrokerageSymbol(string symbol, SecurityType securityType, OptionRight? optionRight, decimal? strike, DateTime? expiryDate, string expectedBrokerageSymbol)
        {
            var leanSymbol = GenerateLeanSymbol(symbol, securityType, optionRight, strike, expiryDate);
            var brokerageSymbol = _symbolMapper.GetBrokerageSymbol(leanSymbol);
            Assert.That(brokerageSymbol, Is.EqualTo(expectedBrokerageSymbol));
        }

        [TestCase("BTCUSDTT")]
        [TestCase("ETH/USDT")]
        public void ThrowExceptionWrongLeanCryptoSymbol(string symbol)
        {
            var leanSymbol = GenerateLeanSymbol(symbol, SecurityType.Crypto);
            Assert.Throws<ArgumentException>(() => _symbolMapper.GetBrokerageSymbol(leanSymbol), $"The symbol '{symbol}' is not found in the brokerage symbol mappings for crypto.");
        }

        private Symbol GenerateLeanSymbol(string symbol, SecurityType securityType, OptionRight? optionRight = OptionRight.Call, decimal? strike = 0m, DateTime? expiryDate = default, OptionStyle? optionStyle = OptionStyle.American)
        {
            switch (securityType)
            {
                case SecurityType.Equity:
                    return Symbol.Create(symbol, SecurityType.Equity, Market.USA);
                case SecurityType.Option:
                    var underlying = Symbol.Create(symbol, SecurityType.Equity, Market.USA);
                    return Symbol.CreateOption(underlying, Market.USA, optionStyle.Value, optionRight.Value, strike.Value, expiryDate.Value);
                case SecurityType.Crypto:
                    return Symbol.Create(symbol, securityType, Market.USA);
                default:
                    throw new NotSupportedException();
            }
        }
    }
}