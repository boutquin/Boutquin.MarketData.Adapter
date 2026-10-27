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
using Boutquin.MarketData.Abstractions.Diagnostics;
using Boutquin.MarketData.Abstractions.Requests;
using Boutquin.MarketData.Abstractions.Results;
using Boutquin.MarketData.Calendars.Holidays;

namespace Boutquin.MarketData.Adapter.Shared;

/// <summary>Creates adapter default calendars with explicit built-in holiday dataset bounds.</summary>
/// <remarks>
/// The qualified kernel holiday datasets cover 2020 through 2030. This factory records those
/// bounds only when it creates a supported built-in calendar. A custom calendar supplied by
/// a consumer has no inferred limits, even when it shares a type or code with a built-in calendar.
/// </remarks>
public static class AdapterCalendarFactory
{
    /// <summary>Creates a kernel calendar and records the bounds of its built-in holiday dataset.</summary>
    /// <param name="code">Kernel publication or market calendar code, such as USSOFR or TARGET.</param>
    /// <returns>A table-backed calendar carrying explicit 2020–2030 dataset bounds, or an unbounded algorithmic/weekend calendar.</returns>
    /// <exception cref="ArgumentException">The code is null, empty, or whitespace.</exception>
    /// <remarks>Coverage helpers emit one non-error range notice using the kernel's Warning severity when a request crosses recorded bounds. USNY and CATO use calendar rules and have no table bound.</remarks>
    public static IBusinessCalendar Create(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        var calendar = HolidayCalendarFactory.Create(code);
        return code is "GBLO" or "TARGET" or "XCME" or "USSF" or "USSOFR" or "CAST"
            ? new BoundedCalendar(calendar) : calendar;
    }

    internal static IReadOnlyList<DataIssue> AddRangeWarning(IBusinessCalendar calendar, DateRange range, IReadOnlyList<DataIssue> issues)
    {
        if (calendar is not BoundedCalendar || (range.From.Year >= 2020 && range.To.Year <= 2030))
        {
            return issues;
        }
        return [.. issues, new DataIssue(new IssueCode("CALENDAR_RANGE_EXCEEDED"), IssueSeverity.Warning,
            "The built-in holiday dataset covers 2020-01-01 through 2030-12-31; dates outside that interval use weekend-only classification.")];
    }

    private sealed class BoundedCalendar(IBusinessCalendar inner) : IBusinessCalendar
    {
        public string Code => inner.Code;
        public int BusinessDaysPerYear => inner.BusinessDaysPerYear;
        public bool IsBusinessDay(DateOnly date) => inner.IsBusinessDay(date);
        public DateOnly Adjust(DateOnly date, BusinessDayAdjustment adjustment) => inner.Adjust(date, adjustment);
        public DateOnly Advance(DateOnly date, int businessDays) => inner.Advance(date, businessDays);
        public IReadOnlyList<DateOnly> BusinessDaysBetween(DateOnly start, DateOnly end) => inner.BusinessDaysBetween(start, end);
    }
}
