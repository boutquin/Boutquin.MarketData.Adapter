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

namespace Boutquin.MarketData.Adapter.ArchitectureTests;

/// <summary>
/// Verifies design rules across all adapter assemblies.
/// </summary>
public sealed class DesignRuleTests : BaseArchitectureTest
{
    [Fact]
    public void AllClasses_ShouldBeSealed()
    {
        foreach (var assembly in AllAssemblies)
        {
            var result = Types
                .InAssembly(assembly)
                .That()
                .AreClasses()
                .And()
                .AreNotStatic()
                .And()
                .AreNotAbstract()
                .Should()
                .BeSealed()
                .GetResult();

            result.IsSuccessful.Should().BeTrue(
                because: $"all non-static non-abstract classes must be sealed [{assembly.GetName().Name}: {GetFailingTypes(result)}]");
        }
    }
}
