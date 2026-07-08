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
using QuantConnect.Securities;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace QuantConnect.Brokerages.Alpaca;

/// <summary>
/// Provides the mapping between Lean symbols and brokerage specific symbols.
/// </summary>
public class AlpacaBrokerageSymbolMapper : ISymbolMapper
{
    /// <summary>
    /// Regular expression for parsing option ticker strings.
    /// The pattern matches the following components:
    /// - symbol: The underlying symbol (e.g., AAPL)
    /// - year: The two-digit expiration year
    /// - month: The two-digit expiration month
    /// - day: The two-digit expiration day
    /// - right: The option right (C for Call, P for Put)
    /// - strike: The eight-digit strike price
    /// </summary>
    /// <example>
    /// Example ticker: AAPL240614C00100000
    /// - symbol: AAPL
    /// - year: 24 (2024)
    /// - month: 06 (June)
    /// - day: 14
    /// - right: C (Call)
    /// - strike: 00100000 (100.000)
    /// </example>
    private static readonly Regex _optionBrokerageTickerRegex = new Regex(
        @"^(?<symbol>[A-Z]+)(?<year>\d{2})(?<month>\d{2})(?<day>\d{2})(?<right>[CP])(?<strike>\d{8})$",
        RegexOptions.Compiled
        );

    /// <summary>
    /// Dictionary that maps Lean symbols to brokerage symbols for crypto assets.
    /// </summary>
    /// <remarks>
    /// The dictionary is initialized in the constructor by retrieving asset information from the Alpaca trading client.
    /// The keys in the dictionary are Lean symbols with slashes removed, and the values are the original brokerage symbols.
    /// </remarks>
    private readonly Dictionary<string, string> _brokerageSymbolByLeanSymbol = [];

    /// <summary>
    /// Represents a set of supported security types.
    /// </summary>
    /// <remarks>
    /// This HashSet contains the supported security types that are allowed within the system.
    /// </remarks>
    public readonly HashSet<SecurityType> SupportedSecurityType = new() { SecurityType.Equity, SecurityType.Option, SecurityType.Crypto };

    /// <summary>
    /// Initializes a new instance of the <see cref="AlpacaBrokerageSymbolMapper"/> class.
    /// </summary>
    /// <param name="alpacaTradingClient">The Alpaca trading client used to retrieve asset information.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="alpacaTradingClient"/> is null.</exception>
    /// <remarks>
    /// The constructor retrieves the crypto asset information from the Alpaca trading client and registers
    /// the symbol properties of the pairs missing from the database so they resolve as tradable securities.
    /// This is done eagerly (rather than lazily) so the registration is in place before the algorithm's
    /// Initialize() runs, which may AddCrypto pairs (e.g. USDC/USD) that aren't in the bundled database.
    /// </remarks>
    public AlpacaBrokerageSymbolMapper(IAlpacaTradingClient alpacaTradingClient)
    {
        var symbolPropertiesDatabase = SymbolPropertiesDatabase.FromDataFolder();
        var existingCryptoSymbols = symbolPropertiesDatabase.GetSymbolPropertiesList(Market.Coinbase, SecurityType.Crypto)
            .Select(entry => entry.Key.Symbol)
            .ToHashSet();

        var res = alpacaTradingClient.ListAssetsAsync(new AssetsRequest() { AssetClass = AssetClass.Crypto }).SynchronouslyAwaitTaskResult();

        foreach (var asset in res)
        {
            var leanTicker = asset.Symbol.Replace("/", string.Empty);
            _brokerageSymbolByLeanSymbol[leanTicker] = asset.Symbol;

            // Alpaca's tradable crypto universe is not fully covered by the bundled symbol properties
            // (e.g. USDC/USD). Register only the pairs that are missing so they resolve as tradable
            // securities, without overriding the existing curated entries.
            if (!existingCryptoSymbols.Contains(leanTicker))
            {
                RegisterSymbolProperties(symbolPropertiesDatabase, leanTicker, asset);
            }
        }
    }

    /// <summary>
    /// Registers the symbol properties for an Alpaca crypto asset into the symbol properties database
    /// under the crypto market, using Alpaca's own trading parameters.
    /// </summary>
    private static void RegisterSymbolProperties(SymbolPropertiesDatabase symbolPropertiesDatabase, string leanTicker, IAsset asset)
    {
        // brokerage symbol is "<base>/<quote>", e.g. "USDC/USD"
        var parts = asset.Symbol.Split('/');
        var quoteCurrency = parts.Length == 2 ? parts[1] : Currencies.USD;

        var symbolProperties = new SymbolProperties(
            description: string.IsNullOrEmpty(asset.Name) ? leanTicker : asset.Name,
            quoteCurrency: quoteCurrency,
            contractMultiplier: 1,
            minimumPriceVariation: asset.PriceIncrement ?? 0.01m,
            lotSize: asset.MinTradeIncrement ?? 0.00000001m,
            marketTicker: asset.Symbol,
            minimumOrderSize: asset.MinOrderSize);

        symbolPropertiesDatabase.SetEntry(Market.Coinbase, leanTicker, SecurityType.Crypto, symbolProperties);
    }

    /// <inheritdoc cref="ISymbolMapper.GetBrokerageSymbol(Symbol)"/>
    public string GetBrokerageSymbol(Symbol symbol) => symbol.SecurityType switch
    {
        // Equity tickers change over the life of a SID (e.g. GOOCV -> GOOG). Resolve the
        // ticker that is current today, since Symbol.Value can still carry the old one.
        SecurityType.Equity => SecurityIdentifier.Ticker(symbol, DateTime.UtcNow),
        SecurityType.Option => GenerateBrokerageOptionSymbol(symbol),
        SecurityType.Crypto => _brokerageSymbolByLeanSymbol.TryGetValue(symbol.Value, out var cryptoSymbol)
        ? cryptoSymbol 
        : throw new ArgumentException($"The symbol '{symbol.Value}' is not found in the brokerage symbol mappings for crypto."),
        _ => throw new NotSupportedException($"{nameof(AlpacaBrokerageSymbolMapper)}.{nameof(GetBrokerageSymbol)}: The security type '{symbol.SecurityType}' is not supported.")
    };

    /// <summary>
    /// Converts a brokerage asset class and symbol to a Lean <see cref="Symbol"/>.
    /// </summary>
    /// <param name="brokerageAssetClass">The asset class from the brokerage.</param>
    /// <param name="brokerageSymbol">The symbol used by the brokerage.</param>
    /// <returns>The Lean <see cref="Symbol"/> corresponding to the given brokerage asset class and symbol.</returns>
    /// <exception cref="NotSupportedException">Thrown when the asset class is not supported.</exception>
    public Symbol GetLeanSymbol(AssetClass? brokerageAssetClass, string brokerageSymbol)
    {
        switch (brokerageAssetClass)
        {
            case AssetClass.UsEquity:
                return Symbol.Create(brokerageSymbol, SecurityType.Equity, Market.USA);
            case AssetClass.UsOption:
                return ParseOptionTicker(brokerageSymbol);
            case AssetClass.Crypto:
                return Symbol.Create(brokerageSymbol.Replace("/", ""), SecurityType.Crypto, Market.Coinbase);
            default:
                throw new NotSupportedException($"Conversion for the asset class '{brokerageAssetClass}' is not supported.");
        }
    }

    /// <inheritdoc cref="ISymbolMapper.GetLeanSymbol(string, SecurityType, string, DateTime, decimal, OptionRight)"/>
    public Symbol GetLeanSymbol(string brokerageSymbol, SecurityType securityType, string market, DateTime expirationDate = default, decimal strike = 0, OptionRight optionRight = OptionRight.Call)
    {
        switch (securityType)
        {
            case SecurityType.Option:
                var underlying = Symbol.Create(brokerageSymbol, SecurityType.Equity, market);
                return Symbol.CreateOption(underlying, market, SecurityType.Option.DefaultOptionStyle(), optionRight, strike, expirationDate);
            default:
                throw new NotImplementedException($"{nameof(AlpacaBrokerageSymbolMapper)}.{nameof(GetLeanSymbol)}: " +
                    $"The security type '{securityType}' with brokerage symbol '{brokerageSymbol}' is not supported.");
        }
    }

    /// <summary>
    /// Parses an option ticker string into a Symbol object.
    /// </summary>
    /// <param name="brokerageTicker">The option ticker string to parse.</param>
    /// <returns>A Symbol object representing the parsed option ticker.</returns>
    /// <exception cref="ArgumentException">Thrown when the ticker format is invalid.</exception>
    public static Symbol ParseOptionTicker(string brokerageTicker)
    {
        var match = _optionBrokerageTickerRegex.Match(brokerageTicker);

        if (!match.Success)
        {
            throw new ArgumentException("Invalid ticker format.");
        }

        var ticker = match.Groups["symbol"].Value;
        var expiryDate = ParseDate(match.Groups["year"].Value, match.Groups["month"].Value, match.Groups["day"].Value);
        var optionRight = match.Groups["right"].Value == "C" ? OptionRight.Call : OptionRight.Put;
        var strike = decimal.Parse(match.Groups["strike"].Value) / 1000m;

        var underlying = Symbol.Create(ticker, SecurityType.Equity, Market.USA);
        return Symbol.CreateOption(underlying, Market.USA, SecurityType.Option.DefaultOptionStyle(), optionRight, strike, expiryDate);
    }

    /// <summary>
    /// Parses the date components from strings into a DateTime object.
    /// </summary>
    /// <param name="year">The year component as a string.</param>
    /// <param name="month">The month component as a string.</param>
    /// <param name="day">The day component as a string.</param>
    /// <returns>A DateTime object representing the parsed date.</returns>
    private static DateTime ParseDate(string year, string month, string day)
    {
        int fullYear = int.Parse(year) + 2000;
        int monthInt = int.Parse(month);
        int dayInt = int.Parse(day);
        return new DateTime(fullYear, monthInt, dayInt);
    }

    /// <summary>
    /// Generates a brokerage option symbol based on the given symbol.
    /// </summary>
    /// <param name="symbol">The option symbol containing the necessary details.</param>
    /// <returns>A string representing the brokerage option symbol.</returns>
    /// <exception cref="ArgumentException">Thrown when the provided symbol is not of type Option.</exception>
    private string GenerateBrokerageOptionSymbol(Symbol symbol)
    {
        if (symbol.SecurityType != SecurityType.Option)
        {
            throw new ArgumentException($"{nameof(AlpacaBrokerageSymbolMapper)}.{nameof(GenerateBrokerageOptionSymbol)}: The provided symbol must be of type Option.", nameof(symbol));
        }

        var strikePriceString = (Convert.ToInt32(symbol.ID.StrikePrice * 1000)).ToStringInvariant("D8");

        return $"{SecurityIdentifier.Ticker(symbol.Underlying, DateTime.UtcNow)}{symbol.ID.Date:yyMMdd}{symbol.ID.OptionRight.ToString()[0]}{strikePriceString}";
    }
}
