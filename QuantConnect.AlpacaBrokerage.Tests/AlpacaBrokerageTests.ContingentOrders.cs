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

using NUnit.Framework;
using QuantConnect.Tests;
using QuantConnect.Tests.Brokerages;

namespace QuantConnect.Brokerages.Alpaca.Tests
{
    public partial class AlpacaBrokerageTests
    {
        private static readonly OrderTestParameters ContingentLimit = new LimitOrderTestParameters(Symbols.AAPL, 1000m, 100m);
        private static readonly OrderTestParameters ContingentStop = new StopMarketOrderTestParameters(Symbols.AAPL, 1000m, 50m);
        private static readonly OrderTestParameters ContingentMarket = new MarketOrderTestParameters(Symbols.AAPL);

        /// <summary>
        /// Bracket and oto order classes, resting: the prices are far from the market. The oco order class exits an existing position
        /// </summary>
        private static TestCaseData[] RestingContingentOrders => new[]
        {
            new TestCaseData(ContingentOrderTestParameters.OneTriggersOther(ContingentLimit, ContingentLimit)),
            new TestCaseData(ContingentOrderTestParameters.Bracket(ContingentLimit, ContingentLimit, ContingentStop))
        };

        /// <summary>
        /// Bracket and oto order classes where the entry fills right away
        /// </summary>
        private static TestCaseData[] TriggeredContingentOrders => new[]
        {
            new TestCaseData(ContingentOrderTestParameters.OneTriggersOther(ContingentMarket, ContingentLimit)),
            new TestCaseData(ContingentOrderTestParameters.Bracket(ContingentMarket, ContingentLimit, ContingentStop))
        };

        [Test, Explicit("Requires an Alpaca account"), TestCaseSource(nameof(RestingContingentOrders))]
        public override void ContingentOrdersCancel(ContingentOrderTestParameters parameters)
        {
            base.ContingentOrdersCancel(parameters);
        }

        [Test, Explicit("Requires an Alpaca account"), TestCaseSource(nameof(TriggeredContingentOrders))]
        public override void ContingentOrdersTrigger(ContingentOrderTestParameters parameters)
        {
            base.ContingentOrdersTrigger(parameters);
        }
    }
}
