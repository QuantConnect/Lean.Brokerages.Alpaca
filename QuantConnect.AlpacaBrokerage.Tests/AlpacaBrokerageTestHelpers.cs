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

using QuantConnect.Configuration;
using System;

namespace QuantConnect.Brokerages.Alpaca.Tests;

public static class AlpacaBrokerageTestHelpers
{
    /// <summary>
    /// Retrieves configuration parameters for Alpaca API.
    /// </summary>
    /// <param name="isValidateOnEmpty">
    /// A boolean indicating whether to validate the configuration parameters for being non-empty.
    /// If set to <c>true</c>, the method will throw an exception if any parameter is empty or null.
    /// </param>
    /// <returns>
    /// A tuple containing the following configuration parameters:
    /// <list type="bullet">
    /// <item>
    /// <term>ApiKey</term>
    /// <description>The Alpaca API key ID.</description>
    /// </item>
    /// <item>
    /// <term>ApiKeySecret</term>
    /// <description>The Alpaca API secret key.</description>
    /// </item>
    /// <item>
    /// <term>DataFeedProvider</term>
    /// <description>The Alpaca data feed provider.</description>
    /// </item>
    /// <item>
    /// <term>IsPaperTrading</term>
    /// <description>A boolean indicating if paper trading is used.</description>
    /// </item>
    /// </list>
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="isValidateOnEmpty"/> is <c>true</c> and any configuration parameter is null or empty.
    /// </exception>
    public static (string ApiKey, string ApiKeySecret, bool IsPapperTrading) GetConfigParameters(bool isValidateOnEmpty = true)
    {
        var (apiKey, apiKeySecret, isPaperTrading) = (Config.Get("alpaca-api-key"), Config.Get("alpaca-api-secret"), Config.GetBool("alpaca-paper-trading"));

        if (!isValidateOnEmpty)
        {
            return (apiKey, apiKeySecret, isPaperTrading);
        }

        if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(apiKeySecret))
        {
            throw new ArgumentNullException("'API Key' or 'Secret Key' or 'Data Feed Provider' cannot be null or empty. Please check your configuration.");
        }

        return (apiKey, apiKeySecret, isPaperTrading);

    }
}
