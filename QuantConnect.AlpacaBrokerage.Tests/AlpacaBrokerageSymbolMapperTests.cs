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
        private AlpacaBrokerageSymbolMapper _symbolMapper = new();

        [TestCase(AssetClass.UsOption, "AAPL240614C00100000", "AAPL", "2024/06/14", OptionRight.Call, 100)]
        [TestCase(AssetClass.UsOption, "AAPL240614P00100000", "AAPL", "2024/06/14", OptionRight.Put, 100)]
        [TestCase(AssetClass.UsOption, "AAPL240614C00235000", "AAPL", "2024/06/14", OptionRight.Call, 235)]
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
        public void ReturnsCorrectBrokerageSymbol()
        {

        }
    }
}