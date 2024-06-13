﻿/*
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

using Alpaca.Markets;
using System;
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


    public string GetBrokerageSymbol(Symbol symbol)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Converts a brokerage asset class and symbol to a Lean <see cref="Symbol"/>.
    /// </summary>
    /// <param name="brokerageAssetClass">The asset class from the brokerage.</param>
    /// <param name="brokerageSymbol">The symbol used by the brokerage.</param>
    /// <returns>The Lean <see cref="Symbol"/> corresponding to the given brokerage asset class and symbol.</returns>
    /// <exception cref="NotSupportedException">Thrown when the asset class is not supported.</exception>
    public Symbol GetLeanSymbol(AssetClass brokerageAssetClass, string brokerageSymbol)
    {
        switch (brokerageAssetClass)
        {
            case AssetClass.UsEquity:
                return Symbol.Create(brokerageSymbol, SecurityType.Equity, Market.USA);
            case AssetClass.UsOption:
                return ParseOptionTicker(brokerageSymbol);
            //case AssetClass.Crypto:
            //    break;
            default:
                throw new NotSupportedException($"Conversion for the asset class '{brokerageAssetClass}' is not supported.");
        }
    }

    public Symbol GetLeanSymbol(string brokerageSymbol, SecurityType securityType, string market, DateTime expirationDate = default, decimal strike = 0, OptionRight optionRight = OptionRight.Call)
    {
        throw new NotImplementedException();
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
}