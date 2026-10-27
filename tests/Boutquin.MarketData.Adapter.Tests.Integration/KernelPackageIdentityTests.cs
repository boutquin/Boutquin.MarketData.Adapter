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

using System.Reflection;
using System.Xml.Linq;

using Boutquin.MarketData.Abstractions.Requests;
using Boutquin.MarketData.Calendars;
using Boutquin.MarketData.Orchestration.Pipeline;
using Boutquin.MarketData.Storage.RawDocuments;
using Boutquin.MarketData.Transport.Http;

using FluentAssertions;

using Xunit;

namespace Boutquin.MarketData.Adapter.Tests.Integration;

public sealed class KernelPackageIdentityTests
{
    private const string ExpectedKernelVersion = "2.0.0-rc.20261027.4";
    private const string ExpectedKernelCommit = "58622453a1456c9659b34a2a705dad62d867a86d";

    private static readonly Type[] s_kernelAssemblyAnchors =
    [
        typeof(DividendHistoryRequest),
        typeof(WeekendOnlyCalendar),
        typeof(ResilientHttpDataTransport),
        typeof(FileRawDocumentStore),
        typeof(DataPipeline),
        typeof(Boutquin.MarketData.DependencyInjection.ServiceCollectionExtensions),
    ];

    [Fact]
    public void Integration_lane_consumes_one_qualified_kernel_identity()
    {
        var repositoryRoot = FindRepositoryRoot();
        var document = XDocument.Load(Path.Combine(repositoryRoot, "Directory.Packages.props"));
        var properties = document.Descendants()
            .Where(element => element.Name.LocalName is "MarketDataKernelVersion" or "MarketDataKernelCommit")
            .ToDictionary(element => element.Name.LocalName, element => element.Value, StringComparer.Ordinal);

        properties.Should().ContainKey("MarketDataKernelVersion")
            .WhoseValue.Should().Be(ExpectedKernelVersion);
        properties.Should().ContainKey("MarketDataKernelCommit")
            .WhoseValue.Should().Be(ExpectedKernelCommit);

        var kernelPins = document.Descendants()
            .Where(element => element.Name.LocalName == "PackageVersion")
            .Where(element => ((string?)element.Attribute("Include"))?.StartsWith(
                "Boutquin.MarketData.", StringComparison.Ordinal) == true)
            .ToList();
        kernelPins.Should().HaveCount(6);
        kernelPins.Should().OnlyContain(
            element => (string?)element.Attribute("Version") == "$(MarketDataKernelVersion)",
            "every kernel package must resolve from the same explicit qualified identity");

        var expectedInformationalVersion = $"{ExpectedKernelVersion}+{ExpectedKernelCommit}";
        var kernelAssemblies = s_kernelAssemblyAnchors.Select(anchor => anchor.Assembly).Distinct().ToList();
        kernelAssemblies.Should().HaveCount(6);
        kernelAssemblies.Select(assembly =>
                assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion)
            .Should().OnlyContain(
                informationalVersion => informationalVersion == expectedInformationalVersion,
                $"each loaded kernel assembly must come from kernel commit {ExpectedKernelCommit}");
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Directory.Packages.props"))
                && File.Exists(Path.Combine(current.FullName, "Boutquin.MarketData.Adapter.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the Adapter repository root.");
    }
}
