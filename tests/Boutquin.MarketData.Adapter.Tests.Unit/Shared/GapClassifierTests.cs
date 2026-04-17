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
using Boutquin.MarketData.Adapter.Shared;
using Boutquin.MarketData.Calendars;
using Boutquin.MarketData.Calendars.Holidays;
using FluentAssertions;
using Xunit;

namespace Boutquin.MarketData.Adapter.Tests.Unit.Shared;

public sealed class GapClassifierTests
{
    private static readonly IBusinessCalendar s_weekendOnly = new WeekendOnlyCalendar("TEST");
    private static readonly IBusinessCalendar s_usny = HolidayCalendarFactory.Create("USNY");

    [Fact]
    public void No_gaps_when_all_business_days_present()
    {
        // Mon 2024-01-08 through Fri 2024-01-12 — 5 business days
        var range = new DateRange(new DateOnly(2024, 1, 8), new DateOnly(2024, 1, 12));
        var returnedDates = new HashSet<DateOnly>
        {
            new(2024, 1, 8),
            new(2024, 1, 9),
            new(2024, 1, 10),
            new(2024, 1, 11),
            new(2024, 1, 12),
        };

        var issues = GapClassifier.Classify(s_weekendOnly, range, returnedDates);

        issues.Should().BeEmpty();
    }

    [Fact]
    public void Weekend_gaps_are_not_flagged()
    {
        // Sat 2024-01-06 through Sun 2024-01-07 — 0 business days
        var range = new DateRange(new DateOnly(2024, 1, 6), new DateOnly(2024, 1, 7));
        var returnedDates = new HashSet<DateOnly>();

        var issues = GapClassifier.Classify(s_weekendOnly, range, returnedDates);

        issues.Should().BeEmpty();
    }

    [Fact]
    public void Holiday_gaps_are_not_flagged()
    {
        // Christmas 2024 falls on Wed Dec 25 — USNY holiday
        var range = new DateRange(new DateOnly(2024, 12, 24), new DateOnly(2024, 12, 26));
        // Return Tue 24th and Thu 26th, skip Wed 25th (Christmas)
        var returnedDates = new HashSet<DateOnly>
        {
            new(2024, 12, 24),
            new(2024, 12, 26),
        };

        var issues = GapClassifier.Classify(s_usny, range, returnedDates);

        issues.Should().BeEmpty();
    }

    [Fact]
    public void Missing_business_day_produces_unexpected_gap()
    {
        // Mon-Fri, but missing Thursday
        var range = new DateRange(new DateOnly(2024, 1, 8), new DateOnly(2024, 1, 12));
        var returnedDates = new HashSet<DateOnly>
        {
            new(2024, 1, 8),
            new(2024, 1, 9),
            new(2024, 1, 10),
            // Thu 2024-01-11 missing
            new(2024, 1, 12),
        };

        var issues = GapClassifier.Classify(s_weekendOnly, range, returnedDates);

        issues.Should().ContainSingle();
        issues[0].Code.Should().Be(new IssueCode("UNEXPECTED_GAP"));
        issues[0].Severity.Should().Be(IssueSeverity.Warning);
        issues[0].Message.Should().Contain("2024-01-11");
    }

    [Fact]
    public void Multiple_missing_business_days_produce_multiple_issues()
    {
        // Mon-Fri, only Monday present
        var range = new DateRange(new DateOnly(2024, 1, 8), new DateOnly(2024, 1, 12));
        var returnedDates = new HashSet<DateOnly>
        {
            new(2024, 1, 8),
        };

        var issues = GapClassifier.Classify(s_weekendOnly, range, returnedDates);

        issues.Should().HaveCount(4);
        issues.Should().AllSatisfy(i => i.Code.Should().Be(new IssueCode("UNEXPECTED_GAP")));
    }

    [Fact]
    public void Range_spanning_weekend_only_flags_missing_weekdays()
    {
        // Thu Jan 11 through Tue Jan 16 — business days: Thu, Fri, Mon, Tue
        var range = new DateRange(new DateOnly(2024, 1, 11), new DateOnly(2024, 1, 16));
        // Return Thu and Tue only, missing Fri and Mon
        var returnedDates = new HashSet<DateOnly>
        {
            new(2024, 1, 11),
            new(2024, 1, 16),
        };

        var issues = GapClassifier.Classify(s_weekendOnly, range, returnedDates);

        issues.Should().HaveCount(2);
        issues.Should().Contain(i => i.Message.Contains("2024-01-12")); // Friday
        issues.Should().Contain(i => i.Message.Contains("2024-01-15")); // Monday
    }

    [Fact]
    public void Range_with_no_business_days_produces_no_issues()
    {
        // Weekend-only range — no business days expected, so no gaps
        var range = new DateRange(new DateOnly(2024, 1, 13), new DateOnly(2024, 1, 14));
        var returnedDates = new HashSet<DateOnly>();

        var issues = GapClassifier.Classify(s_weekendOnly, range, returnedDates);

        issues.Should().BeEmpty();
    }
}
