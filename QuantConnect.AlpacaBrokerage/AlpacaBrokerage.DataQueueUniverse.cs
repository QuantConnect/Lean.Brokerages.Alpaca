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
using QuantConnect.Logging;
using System.Threading.Tasks;
using QuantConnect.Interfaces;
using System.Collections.Generic;
using System.Collections.Concurrent;

namespace QuantConnect.Brokerages.Alpaca;

public partial class AlpacaBrokerage : IDataQueueUniverseProvider
{
    /// <summary>
    /// Method returns a collection of Symbols that are available at the data source.
    /// </summary>
    /// <param name="symbol">Symbol to lookup</param>
    /// <param name="includeExpired">Include expired contracts</param>
    /// <param name="securityCurrency">Expected security currency(if any)</param>
    /// <returns>Enumerable of Symbols, that are associated with the provided Symbol</returns>
    public IEnumerable<Symbol> LookupSymbols(Symbol symbol, bool includeExpired, string securityCurrency = null)
    {
        if (!symbol.SecurityType.IsOption())
        {
            Log.Error("The provided symbol is not an option. SecurityType: " + symbol.SecurityType);
            return Enumerable.Empty<Symbol>();
        }
        var blockingOptionCollection = new BlockingCollection<Symbol>();

        Task.Run(async () =>
        {
            var underlying = symbol.Underlying.Value;

            var nextPageToken = default(string);
            var optionContractRequest = new OptionContractsRequest(symbol.Underlying.Value) { ExpirationDateGreaterThanOrEqualTo = DateOnly.FromDateTime(DateTime.UtcNow) };
            optionContractRequest.Pagination.Size = 10_000;
            do
            {
                var response = await AlpacaTradingClient.ListOptionContractsAsync(optionContractRequest);
                nextPageToken = response.NextPageToken;
                foreach (var res in response.Items)
                {
                    blockingOptionCollection.Add(_symbolMapper.GetLeanSymbol(AssetClass.UsOption, res.Symbol));
                }

                optionContractRequest.Pagination.Token = nextPageToken;

            } while (!string.IsNullOrEmpty(nextPageToken));
        }).ContinueWith(_ => blockingOptionCollection.CompleteAdding());

        var options = blockingOptionCollection.GetConsumingEnumerable();

        // Validate if the collection contains at least one successful response from history.
        if (!options.Any())
        {
            return null;
        }

        return options;
    }

    /// <summary>
    /// Returns whether selection can take place or not.
    /// </summary>
    /// <remarks>This is useful to avoid a selection taking place during invalid times, for example IB reset times or when not connected,
    /// because if allowed selection would fail since IB isn't running and would kill the algorithm</remarks>
    /// <returns>True if selection can take place</returns>
    public bool CanPerformSelection()
    {
        return IsConnected;
    }
}
