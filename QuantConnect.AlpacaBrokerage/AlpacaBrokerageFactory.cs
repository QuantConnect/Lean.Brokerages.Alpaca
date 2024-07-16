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
using QuantConnect.Util;
using QuantConnect.Packets;
using QuantConnect.Interfaces;
using QuantConnect.Securities;
using System.Collections.Generic;
using QuantConnect.Configuration;

namespace QuantConnect.Brokerages.Alpaca
{
    /// <summary>
    /// Factory type for the <see cref="AlpacaBrokerage"/>
    /// </summary>
    public class AlpacaBrokerageFactory : BrokerageFactory
    {
        /// <summary>
        /// Gets the brokerage data required to run the brokerage from configuration/disk
        /// </summary>
        /// <remarks>
        /// The implementation of this property will create the brokerage data dictionary required for
        /// running live jobs. See <see cref="IJobQueueHandler.NextJob"/>
        /// </remarks>
        public override Dictionary<string, string> BrokerageData => new Dictionary<string, string>
        {
            { "alpaca-access-token", Config.Get("alpaca-access-token") },

            { "alpaca-api-key-id", Config.Get("alpaca-api-key-id") },
            { "alpaca-api-secret-key", Config.Get("alpaca-api-secret-key") },

            { "alpaca-use-paper-trading", Config.Get("alpaca-use-paper-trading") },
        };

        /// <summary>
        /// Initializes a new instance of the <see cref="AlpacaBrokerageFactory"/> class
        /// </summary>
        public AlpacaBrokerageFactory() : base(typeof(AlpacaBrokerage))
        {
        }

        /// <summary>
        /// Gets a brokerage model that can be used to model this brokerage's unique behaviors
        /// </summary>
        /// <param name="orderProvider">The order provider</param>
        public override IBrokerageModel GetBrokerageModel(IOrderProvider orderProvider) => new AlpacaBrokerageModel();

        /// <summary>
        /// Creates a new IBrokerage instance
        /// </summary>
        /// <param name="job">The job packet to create the brokerage for</param>
        /// <param name="algorithm">The algorithm instance</param>
        /// <returns>A new brokerage instance</returns>
        public override IBrokerage CreateBrokerage(LiveNodePacket job, IAlgorithm algorithm)
        {
            // used for data
            job.BrokerageData.TryGetValue("alpaca-api-key-id", out var apiKey);
            job.BrokerageData.TryGetValue("alpaca-api-secret-key", out var secretKey);

            // optionally, required for trading
            job.BrokerageData.TryGetValue("alpaca-access-token", out var accessToken);

            var usePaperTrading = Convert.ToBoolean(job.BrokerageData["alpaca-use-paper-trading"]);
            var alpacaBrokerage = new AlpacaBrokerage(apiKey, secretKey, accessToken, usePaperTrading, algorithm);

            if (!string.IsNullOrEmpty(secretKey))
            {
                Composer.Instance.AddPart<IDataQueueHandler>(alpacaBrokerage);
            }
            return alpacaBrokerage;
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public override void Dispose()
        {
        }
    }
}