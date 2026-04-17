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
/// Thin adapter-facing wrapper over <see cref="CoverageComputation.ComputeWithGaps"/>.
/// Delegates all coverage computation and gap classification to the canonical implementation
/// in Abstractions, eliminating logic duplication between adapters and the pipeline.
/// </summary>
public static class AdapterCoverageHelper
{
    /// <summary>
    /// Computes calendar-aware coverage and classifies any gaps.
    /// Delegates to <see cref="CoverageComputation.ComputeWithGaps"/>.
    /// </summary>
    /// <param name="calendar">The business calendar for counting expected trading days.</param>
    /// <param name="range">The requested date range (inclusive).</param>
    /// <param name="frequency">The data sampling frequency.</param>
    /// <param name="returnedDates">The set of dates for which data was actually returned.</param>
    /// <returns>A tuple of the computed coverage and any gap-related issues.</returns>
    public static (DataCoverage Coverage, IReadOnlyList<DataIssue> GapIssues) Compute(
        IBusinessCalendar calendar,
        DateRange range,
        DataFrequency frequency,
        IReadOnlySet<DateOnly> returnedDates) =>
        CoverageComputation.ComputeWithGaps(calendar, range, frequency, returnedDates);
}
