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
/// Verifies dependency isolation between adapter assemblies.
/// </summary>
public sealed class DependencyTests : BaseArchitectureTest
{
    [Fact]
    public void NoAdapter_ShouldDependOnAnyOtherAdapter()
    {
        var adapterAssemblyNames = AllAdapterAssemblies
            .Select(a => a.GetName().Name!)
            .ToArray();

        foreach (var assembly in AllAdapterAssemblies)
        {
            var otherAdapters = adapterAssemblyNames
                .Where(name => name != assembly.GetName().Name)
                .ToArray();

            var result = Types
                .InAssembly(assembly)
                .Should()
                .NotHaveDependencyOnAny(otherAdapters)
                .GetResult();

            result.IsSuccessful.Should().BeTrue(
                because: $"adapter {assembly.GetName().Name} must not depend on any other adapter [{GetFailingTypes(result)}]");
        }
    }

    [Fact]
    public void NoAdapter_ShouldDependOnOrchestrationOrDi()
    {
        foreach (var assembly in AllAdapterAssemblies)
        {
            var result = Types
                .InAssembly(assembly)
                .Should()
                .NotHaveDependencyOnAny(
                    "Boutquin.MarketData.Orchestration",
                    "Boutquin.MarketData.DependencyInjection")
                .GetResult();

            result.IsSuccessful.Should().BeTrue(
                because: $"adapters must not depend on Orchestration or DI [{assembly.GetName().Name}: {GetFailingTypes(result)}]");
        }
    }

    [Fact]
    public void NoAssembly_ShouldDependOnAnalyticsOrTradingOrOptionPricing()
    {
        foreach (var assembly in AllAssemblies)
        {
            var result = Types
                .InAssembly(assembly)
                .Should()
                .NotHaveDependencyOnAny(
                    "Boutquin.Analytics",
                    "Boutquin.Trading",
                    "Boutquin.OptionPricing")
                .GetResult();

            result.IsSuccessful.Should().BeTrue(
                because: $"Adapter assemblies must not depend on Analytics, Trading, or OptionPricing [{assembly.GetName().Name}: {GetFailingTypes(result)}]");
        }
    }
}
