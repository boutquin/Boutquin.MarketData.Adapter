// Copyright (c) 2026 Pierre G. Boutquin. All rights reserved.
//
//   Licensed under the Apache License, Version 2.0 (the "License").
//   You may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//
//       http://www.apache.org/licenses/LICENSE-2.0
//
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//
//   See the License for the specific language governing permissions and
//   limitations under the License.
//

using System.Xml.Linq;

namespace Boutquin.MarketData.Adapter.ArchitectureTests;

public sealed class AssemblyRosterTests : BaseArchitectureTest
{
    [Fact]
    public void Architecture_roster_covers_every_source_or_packaged_adapter()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Boutquin.MarketData.Adapter.slnx")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull();
        var root = directory!.FullName;
        var sources = Path.Combine(root, "src");
        string[] expected;
        if (Directory.Exists(sources))
        {
            expected = Directory.EnumerateFiles(sources, "*.csproj", SearchOption.AllDirectories)
                .Select(path => XDocument.Load(path).Descendants("PackageId").Single().Value)
                .ToArray();
        }
        else
        {
            expected = XDocument.Load(Path.Combine(root, "tests", "Architecture", "Architecture.csproj"))
                .Descendants("PackageReference")
                .Select(element => (string?)element.Attribute("Include"))
                .Where(name => name?.StartsWith("Boutquin.MarketData.Adapter.", StringComparison.Ordinal) == true)
                .Select(name => name!)
                .ToArray();
        }

        expected.Should().NotBeEmpty();
        AllAssemblies.Select(assembly => assembly.GetName().Name).Should().BeEquivalentTo(expected);
    }
}
