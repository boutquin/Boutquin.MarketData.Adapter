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

using Boutquin.MarketData.Abstractions.Calendars;
using Boutquin.MarketData.Abstractions.Requests;
using Boutquin.MarketData.Abstractions.Results;

namespace Boutquin.MarketData.Adapter.Shared;

/// <summary>
/// Classifies missing dates within a requested range as expected (holiday/weekend)
/// or unexpected gaps, based on a business calendar.
/// Delegates to <see cref="CoverageComputation.ClassifyGaps"/> for the canonical implementation.
/// </summary>
public static class GapClassifier
{
    /// <summary>
    /// Classifies missing dates as expected (holiday/weekend) or unexpected gaps.
    /// </summary>
    /// <param name="calendar">The business calendar governing publication days for the data source.</param>
    /// <param name="range">The requested date range (inclusive).</param>
    /// <param name="returnedDates">The set of dates for which data was actually returned.</param>
    /// <returns>A list of <see cref="DataIssue"/> entries for each unexpected gap.</returns>
    public static IReadOnlyList<DataIssue> Classify(
        IBusinessCalendar calendar,
        DateRange range,
        IReadOnlySet<DateOnly> returnedDates) =>
        CoverageComputation.ClassifyGaps(calendar, range, returnedDates);
}
