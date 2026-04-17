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
using Boutquin.MarketData.Adapter.Shared;
using Boutquin.MarketData.Calendars;
using Boutquin.MarketData.Calendars.Holidays;
using FluentAssertions;
using Xunit;

namespace Boutquin.MarketData.Adapter.Tests.Unit.Shared;

public sealed class AdapterCoverageHelperTests
{
    private static readonly IBusinessCalendar s_weekendOnly = new WeekendOnlyCalendar("TEST");
    private static readonly IBusinessCalendar s_usny = HolidayCalendarFactory.Create("USNY");

    [Fact]
    public void Full_week_complete_returns_ratio_one()
    {
        // Mon-Fri, all 5 business days present
        var range = new DateRange(new DateOnly(2024, 1, 8), new DateOnly(2024, 1, 12));
        var returnedDates = new HashSet<DateOnly>
        {
            new(2024, 1, 8),
            new(2024, 1, 9),
            new(2024, 1, 10),
            new(2024, 1, 11),
            new(2024, 1, 12),
        };

        var (coverage, gapIssues) = AdapterCoverageHelper.Compute(
            s_weekendOnly, range, DataFrequency.Daily, returnedDates);

        coverage.RequestedPoints.Should().Be(5);
        coverage.ReturnedPoints.Should().Be(5);
        coverage.MissingPoints.Should().Be(0);
        coverage.CoverageRatio.Should().Be(1.0m);
        coverage.Status.Should().Be(CompletenessStatus.Complete);
        gapIssues.Should().BeEmpty();
    }

    [Fact]
    public void Weekend_only_range_returns_complete_with_zero_points()
    {
        // Sat-Sun, 0 expected business days
        var range = new DateRange(new DateOnly(2024, 1, 6), new DateOnly(2024, 1, 7));
        var returnedDates = new HashSet<DateOnly>();

        var (coverage, gapIssues) = AdapterCoverageHelper.Compute(
            s_weekendOnly, range, DataFrequency.Daily, returnedDates);

        coverage.RequestedPoints.Should().Be(0);
        coverage.ReturnedPoints.Should().Be(0);
        coverage.CoverageRatio.Should().Be(1.0m); // 0/0 = 1.0 by convention
        coverage.Status.Should().Be(CompletenessStatus.Complete);
        gapIssues.Should().BeEmpty();
    }

    [Fact]
    public void Missing_business_day_returns_partial_with_gap_issue()
    {
        // Mon-Fri, missing Thursday
        var range = new DateRange(new DateOnly(2024, 1, 8), new DateOnly(2024, 1, 12));
        var returnedDates = new HashSet<DateOnly>
        {
            new(2024, 1, 8),
            new(2024, 1, 9),
            new(2024, 1, 10),
            new(2024, 1, 12),
        };

        var (coverage, gapIssues) = AdapterCoverageHelper.Compute(
            s_weekendOnly, range, DataFrequency.Daily, returnedDates);

        coverage.RequestedPoints.Should().Be(5);
        coverage.ReturnedPoints.Should().Be(4);
        coverage.MissingPoints.Should().Be(1);
        coverage.CoverageRatio.Should().Be(0.8m);
        coverage.Status.Should().Be(CompletenessStatus.Partial);
        gapIssues.Should().ContainSingle();
        gapIssues[0].Code.Should().Be(new IssueCode("UNEXPECTED_GAP"));
    }

    [Fact]
    public void Holiday_does_not_count_as_missing()
    {
        // MLK Day 2024 is Mon Jan 15 — USNY holiday
        // Request Mon Jan 15 through Fri Jan 19 — only 4 business days expected
        var range = new DateRange(new DateOnly(2024, 1, 15), new DateOnly(2024, 1, 19));
        var returnedDates = new HashSet<DateOnly>
        {
            new(2024, 1, 16), // Tue
            new(2024, 1, 17), // Wed
            new(2024, 1, 18), // Thu
            new(2024, 1, 19), // Fri
        };

        var (coverage, gapIssues) = AdapterCoverageHelper.Compute(
            s_usny, range, DataFrequency.Daily, returnedDates);

        coverage.RequestedPoints.Should().Be(4);
        coverage.ReturnedPoints.Should().Be(4);
        coverage.CoverageRatio.Should().Be(1.0m);
        coverage.Status.Should().Be(CompletenessStatus.Complete);
        gapIssues.Should().BeEmpty();
    }

    [Fact]
    public void All_business_days_missing_returns_missing_status()
    {
        // Mon-Fri, 0 returned
        var range = new DateRange(new DateOnly(2024, 1, 8), new DateOnly(2024, 1, 12));
        var returnedDates = new HashSet<DateOnly>();

        var (coverage, gapIssues) = AdapterCoverageHelper.Compute(
            s_weekendOnly, range, DataFrequency.Daily, returnedDates);

        coverage.RequestedPoints.Should().Be(5);
        coverage.ReturnedPoints.Should().Be(0);
        coverage.MissingPoints.Should().Be(5);
        coverage.CoverageRatio.Should().Be(0m);
        coverage.Status.Should().Be(CompletenessStatus.Missing);
        gapIssues.Should().HaveCount(5);
    }

    [Fact]
    public void Weekly_frequency_uses_calendar_week_count()
    {
        // 2 weeks: Jan 8-21 — should be 3 week-points (inclusive boundary)
        var range = new DateRange(new DateOnly(2024, 1, 8), new DateOnly(2024, 1, 21));
        var returnedDates = new HashSet<DateOnly>
        {
            new(2024, 1, 12),
            new(2024, 1, 19),
        };

        var (coverage, _) = AdapterCoverageHelper.Compute(
            s_weekendOnly, range, DataFrequency.Weekly, returnedDates);

        // Weekly: (21-8)/7 + 1 = 1 + 1 = 2 expected (13 days / 7 = 1, + 1)
        coverage.RequestedPoints.Should().Be(2);
        coverage.ReturnedPoints.Should().Be(2);
    }

    [Fact]
    public void Monthly_frequency_uses_month_count()
    {
        // Jan through Mar 2024 — 3 months
        var range = new DateRange(new DateOnly(2024, 1, 1), new DateOnly(2024, 3, 31));
        var returnedDates = new HashSet<DateOnly>
        {
            new(2024, 1, 31),
            new(2024, 2, 29),
            new(2024, 3, 29),
        };

        var (coverage, _) = AdapterCoverageHelper.Compute(
            s_weekendOnly, range, DataFrequency.Monthly, returnedDates);

        coverage.RequestedPoints.Should().Be(3);
        coverage.ReturnedPoints.Should().Be(3);
        coverage.CoverageRatio.Should().Be(1.0m);
    }
}
