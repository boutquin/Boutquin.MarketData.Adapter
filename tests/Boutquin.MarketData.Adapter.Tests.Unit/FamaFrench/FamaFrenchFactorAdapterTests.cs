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

using System.IO.Compression;
using Boutquin.MarketData.Abstractions.Calendars;
using Boutquin.MarketData.Abstractions.Diagnostics;
using Boutquin.MarketData.Abstractions.Provenance;
using Boutquin.MarketData.Abstractions.ReferenceData;
using Boutquin.MarketData.Abstractions.Requests;
using Boutquin.MarketData.Abstractions.Results;
using Boutquin.MarketData.Adapter.FamaFrench;
using Boutquin.MarketData.Adapter.Tests.Shared;
using Boutquin.MarketData.Calendars;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Boutquin.MarketData.Adapter.Tests.Unit.FamaFrench;

public sealed class FamaFrenchFactorAdapterTests
{
    private static readonly FakeClock s_clock = new();

    private static FamaFrenchFactorAdapter CreateAdapter(
        FakeHttpDataTransport transport,
        FakeRawDocumentStore? rawStore = null,
        FamaFrenchOptions? options = null,
        IBusinessCalendar? calendar = null)
    {
        return new FamaFrenchFactorAdapter(
            transport,
            rawStore ?? new FakeRawDocumentStore(),
            s_clock,
            calendar ?? new WeekendOnlyCalendar("TEST"),
            Options.Create(options ?? new FamaFrenchOptions()),
            NullLogger<FamaFrenchFactorAdapter>.Instance);
    }

    private static DateRange MakeRange(int year = 2024, int fromMonth = 1, int fromDay = 1, int toMonth = 1, int toDay = 31) =>
        new(new DateOnly(year, fromMonth, fromDay), new DateOnly(year, toMonth, toDay));

    private static byte[] CreateFamaFrenchZip(string csvContent)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("F-F_Research_Data_Factors_daily.CSV");
            using var writer = new StreamWriter(entry.Open());
            writer.Write(csvContent);
        }

        return ms.ToArray();
    }

    [Fact]
    public void ProviderKey_returns_fama_french() =>
        CreateAdapter(new FakeHttpDataTransport()).ProviderKey.Should().Be(new ProviderCode("fama-french"));

    [Fact]
    public void Priority_returns_100() =>
        CreateAdapter(new FakeHttpDataTransport()).Priority.Should().Be(100);

    [Fact]
    public void CanHandle_returns_true_for_non_empty_dataset()
    {
        var request = new FactorSeriesRequest(new FactorDatasetId("F-F_Research_Data_Factors_daily"), MakeRange());
        CreateAdapter(new FakeHttpDataTransport()).CanHandle(request).Should().BeTrue();
    }

    [Fact]
    public async Task FetchAsync_parses_zip_csv_into_factor_observations()
    {
        var csv = "This file was created by WRDS...\n\n,Mkt-RF,SMB,HML\n20240102,  0.25,  0.10, -0.05\n20240103,  0.35,  0.20, -0.10\n\n Annual Factors...";

        var zipBytes = CreateFamaFrenchZip(csv);
        var transport = new FakeHttpDataTransport();
        transport.RespondToWithBytes("_CSV.zip", zipBytes);
        var adapter = CreateAdapter(transport);

        var request = new FactorSeriesRequest(new FactorDatasetId("F-F_Research_Data_Factors_daily"), MakeRange());
        var result = await adapter.FetchAsync(request, CancellationToken.None);

        result.Payload.Should().Contain(o => o.Date == new DateOnly(2024, 1, 2) && o.FactorName == "Mkt-RF" && o.Value == 0.0025m);
        result.Payload.Should().Contain(o => o.Date == new DateOnly(2024, 1, 2) && o.FactorName == "SMB" && o.Value == 0.001m);
        result.Payload.Should().Contain(o => o.Date == new DateOnly(2024, 1, 2) && o.FactorName == "HML" && o.Value == -0.0005m);
        result.Payload.Should().Contain(o => o.Date == new DateOnly(2024, 1, 3));
    }

    [Fact]
    public async Task FetchAsync_populates_provenance()
    {
        var csv = ",Mkt-RF\n20240102,  0.25\n";
        var zipBytes = CreateFamaFrenchZip(csv);
        var transport = new FakeHttpDataTransport();
        transport.RespondToWithBytes("_CSV.zip", zipBytes);
        var adapter = CreateAdapter(transport);

        var result = await adapter.FetchAsync(new FactorSeriesRequest(new FactorDatasetId("F-F_Research_Data_Factors_daily"), MakeRange()), CancellationToken.None);

        result.Provenance.Should().ContainSingle();
        result.Provenance[0].Provider.Should().Be(new ProviderCode("fama-french"));
    }

    [Fact]
    public async Task FetchAsync_reports_malformed_factor_instead_of_complete_data()
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondToWithBytes("_CSV.zip", CreateFamaFrenchZip(
            ",Mkt-RF,SMB\n20240102,0.25,not-a-number\n"));

        var result = await CreateAdapter(transport).FetchAsync(
            new FactorSeriesRequest(new FactorDatasetId("F-F_Research_Data_Factors_daily"),
                new DateRange(new DateOnly(2024, 1, 2), new DateOnly(2024, 1, 2))),
            CancellationToken.None);

        result.Payload.Should().BeEmpty();
        result.Issues.Should().Contain(i => i.Severity == IssueSeverity.Error);
        result.Coverage.Status.Should().NotBe(CompletenessStatus.Complete);
    }

    [Fact]
    public async Task FetchAsync_reports_row_missing_all_factor_columns()
    {
        var transport = new FakeHttpDataTransport();
        transport.RespondToWithBytes("_CSV.zip", CreateFamaFrenchZip(",Mkt-RF\n20240102,0.25\n20240102\n"));

        var result = await CreateAdapter(transport).FetchAsync(
            new FactorSeriesRequest(new FactorDatasetId("F-F_Research_Data_Factors_daily"),
                new DateRange(new DateOnly(2024, 1, 2), new DateOnly(2024, 1, 2))),
            CancellationToken.None);

        result.Payload.Should().BeEmpty();
        result.Issues.Should().ContainSingle(i => i.Severity == IssueSeverity.Error);
        result.Coverage.Status.Should().NotBe(CompletenessStatus.Complete);
    }

    [Fact]
    public async Task FetchAsync_propagates_caller_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var transport = new FakeHttpDataTransport();
        transport.ThrowOnNext(new OperationCanceledException(cancellation.Token));

        Func<Task> act = () => CreateAdapter(transport).FetchAsync(
            new FactorSeriesRequest(new FactorDatasetId("F-F_Research_Data_Factors_daily"), MakeRange()),
            cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task FetchAsync_does_not_disclose_transport_exception_details()
    {
        var transport = new FakeHttpDataTransport();
        transport.ThrowOnNext(new IOException("credential=/private/keys/sentinel-secret.txt"));
        var result = await CreateAdapter(transport).FetchAsync(
            new FactorSeriesRequest(new FactorDatasetId("F-F_Research_Data_Factors_daily"), MakeRange()),
            CancellationToken.None);

        result.Issues.Should().ContainSingle(i => i.Code == new IssueCode("FETCH_FAILED"));
        result.Issues[0].Message.Should().NotContain("sentinel-secret");
    }

    [Theory]
    [InlineData("../../iex/price")]
    [InlineData("foo?x=1")]
    public async Task FetchAsync_rejects_unsafe_dataset_id_before_transport(string datasetId)
    {
        var transport = new FakeHttpDataTransport();
        var adapter = CreateAdapter(transport);
        var request = new FactorSeriesRequest(new FactorDatasetId(datasetId), MakeRange());

        adapter.CanHandle(request).Should().BeFalse();
        var result = await adapter.FetchAsync(request, CancellationToken.None);
        transport.RequestedUris.Should().BeEmpty();
        result.Issues.Should().ContainSingle(i => i.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void CanHandle_rejects_intraday_frequency()
    {
        var request = new FactorSeriesRequest(new FactorDatasetId("F-F_Research_Data_Factors_daily"),
            MakeRange(), DataFrequency.Intraday);
        CreateAdapter(new FakeHttpDataTransport()).CanHandle(request).Should().BeFalse();
    }

    // ---------- Calendar-aware coverage (feature-calendar-aware-coverage) ----------

    [Fact]
    public async Task FetchAsync_business_day_range_reports_complete_coverage()
    {
        var csv = ",Mkt-RF,SMB,HML\n" +
                  "20240108,  0.25,  0.10, -0.05\n" +
                  "20240109,  0.25,  0.10, -0.05\n" +
                  "20240110,  0.25,  0.10, -0.05\n" +
                  "20240111,  0.25,  0.10, -0.05\n" +
                  "20240112,  0.25,  0.10, -0.05\n";
        var zipBytes = CreateFamaFrenchZip(csv);
        var transport = new FakeHttpDataTransport();
        transport.RespondToWithBytes("_CSV.zip", zipBytes);
        var adapter = CreateAdapter(transport);

        var request = new FactorSeriesRequest(
            new FactorDatasetId("F-F_Research_Data_Factors_daily"),
            new DateRange(new DateOnly(2024, 1, 8), new DateOnly(2024, 1, 12)));

        var result = await adapter.FetchAsync(request, CancellationToken.None);

        result.Coverage.RequestedPoints.Should().Be(5);
        result.Coverage.ReturnedPoints.Should().Be(5);
        result.Coverage.Status.Should().Be(CompletenessStatus.Complete);
        result.Issues.Should().NotContain(i => i.Code == new IssueCode("UNEXPECTED_GAP"));
    }

    [Fact]
    public async Task FetchAsync_weekend_only_range_reports_no_unexpected_gaps()
    {
        var csv = ",Mkt-RF\n";
        var zipBytes = CreateFamaFrenchZip(csv);
        var transport = new FakeHttpDataTransport();
        transport.RespondToWithBytes("_CSV.zip", zipBytes);
        var adapter = CreateAdapter(transport);

        var request = new FactorSeriesRequest(
            new FactorDatasetId("F-F_Research_Data_Factors_daily"),
            new DateRange(new DateOnly(2024, 1, 6), new DateOnly(2024, 1, 7)));

        var result = await adapter.FetchAsync(request, CancellationToken.None);

        result.Coverage.RequestedPoints.Should().Be(0);
        result.Coverage.CoverageRatio.Should().Be(1.0m);
        result.Issues.Should().NotContain(i => i.Code == new IssueCode("UNEXPECTED_GAP"));
    }

    [Fact]
    public async Task FetchAsync_missing_business_day_emits_unexpected_gap()
    {
        var csv = ",Mkt-RF\n" +
                  "20240108,  0.25\n" +
                  "20240109,  0.25\n" +
                  "20240110,  0.25\n";
        var zipBytes = CreateFamaFrenchZip(csv);
        var transport = new FakeHttpDataTransport();
        transport.RespondToWithBytes("_CSV.zip", zipBytes);
        var adapter = CreateAdapter(transport);

        var request = new FactorSeriesRequest(
            new FactorDatasetId("F-F_Research_Data_Factors_daily"),
            new DateRange(new DateOnly(2024, 1, 8), new DateOnly(2024, 1, 12)));

        var result = await adapter.FetchAsync(request, CancellationToken.None);

        result.Coverage.Status.Should().Be(CompletenessStatus.Partial);
        var gaps = result.Issues.Where(i => i.Code == new IssueCode("UNEXPECTED_GAP")).ToList();
        gaps.Should().HaveCount(2);
    }
}
