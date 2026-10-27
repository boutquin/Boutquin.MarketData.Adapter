// Copyright (c) 2026 Pierre G. Boutquin. All rights reserved.
//
//  Licensed under the Apache License, Version 2.0 (the "License").
//  You may not use this file except in compliance with the License.
//  You may obtain a copy of the License at
//
//      http://www.apache.org/licenses/LICENSE-2.0
//
//  Unless required by applicable law or agreed to in writing, software
//  distributed under the License is distributed on an "AS IS" BASIS,
//  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//
//  See the License for the specific language governing permissions and
//  limitations under the License.
//

using Boutquin.MarketData.Abstractions.ReferenceData;

namespace Boutquin.MarketData.Adapter.Tiingo;

/// <summary>
/// Enforces Tiingo's provider-specific ticker grammar before a ticker is interpolated into an
/// authenticated URI path or raw-document key.
/// </summary>
internal static class TiingoTickerValidator
{
    // The raw-document store appends ".json" to the metadata ticker segment. Keeping the
    // ASCII ticker at or below 250 bytes keeps that filesystem component within the portable
    // 255-byte NAME_MAX boundary used by the store's filesystem-backed implementation.
    private const int MaxTickerLength = 250;

    /// <summary>
    /// Rejects the first ticker that is not a bounded Tiingo daily-endpoint path segment.
    /// </summary>
    internal static void ThrowIfInvalid(IEnumerable<Symbol> symbols, string parameterName)
    {
        foreach (var symbol in symbols)
        {
            if (!IsValid(symbol.Ticker))
            {
                throw new ArgumentException(
                    $"Ticker '{symbol.Ticker}' is not valid for the Tiingo daily endpoint.",
                    parameterName);
            }
        }
    }

    /// <summary>
    /// Accepts ASCII letters, digits, dots, and hyphens, with an alphanumeric first and last
    /// character. This covers Tiingo forms such as <c>BRK-B</c> and <c>BF.B</c> while excluding
    /// URI path separators, traversal segments, query delimiters, fragments, and escapes.
    /// </summary>
    private static bool IsValid(string? ticker)
    {
        if (string.IsNullOrEmpty(ticker)
            || ticker.Length > MaxTickerLength
            || !char.IsAsciiLetterOrDigit(ticker[0])
            || !char.IsAsciiLetterOrDigit(ticker[^1]))
        {
            return false;
        }

        foreach (var character in ticker)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-')
            {
                return false;
            }
        }

        return true;
    }
}
