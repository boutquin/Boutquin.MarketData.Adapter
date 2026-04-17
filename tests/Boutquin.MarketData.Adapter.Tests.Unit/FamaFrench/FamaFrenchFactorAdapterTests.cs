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
using Boutquin.MarketData.Abstractions.Requests;
using Boutquin.MarketData.Adapter.FamaFrench;
using Boutquin.MarketData.Adapter.Tests.Shared;
using Boutquin.MarketData.Calendars;
using Boutquin.MarketData.Abstractions.Provenance;
using Boutquin.MarketData.Abstractions.ReferenceData;
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

        result.Payload.Should().HaveCount(2);
        result.Payload[0].Date.Should().Be(new DateOnly(2024, 1, 2));
        result.Payload[0].Factors["Mkt-RF"].Should().Be(0.0025m);
        result.Payload[0].Factors["SMB"].Should().Be(0.001m);
        result.Payload[0].Factors["HML"].Should().Be(-0.0005m);
        result.Payload[1].Date.Should().Be(new DateOnly(2024, 1, 3));
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
}
