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

namespace Boutquin.MarketData.Adapter.ArchitectureTests.Helpers;

/// <summary>
/// Base class providing assembly references for all architecture tests.
/// </summary>
public abstract class BaseArchitectureTest
{
    protected static Assembly SharedAssembly =>
        typeof(Shared.GapClassifier).Assembly;

    protected static Assembly TiingoAssembly =>
        typeof(Tiingo.TiingoOptions).Assembly;

    protected static Assembly FrankfurterAssembly =>
        typeof(Frankfurter.FrankfurterOptions).Assembly;

    protected static Assembly FredAssembly =>
        typeof(Fred.FredOptions).Assembly;

    protected static Assembly BankOfCanadaAssembly =>
        typeof(BankOfCanada.BankOfCanadaOptions).Assembly;

    protected static Assembly BankOfEnglandAssembly =>
        typeof(BankOfEngland.BankOfEnglandOptions).Assembly;

    protected static Assembly EcbAssembly =>
        typeof(Ecb.EcbOptions).Assembly;

    protected static Assembly NewYorkFedAssembly =>
        typeof(NewYorkFed.NewYorkFedOptions).Assembly;

    protected static Assembly UsTreasuryAssembly =>
        typeof(UsTreasury.UsTreasuryOptions).Assembly;

    protected static Assembly FamaFrenchAssembly =>
        typeof(FamaFrench.FamaFrenchOptions).Assembly;

    protected static Assembly CmeAssembly =>
        typeof(Cme.CmeOptions).Assembly;

    protected static Assembly TwelveDataAssembly =>
        typeof(TwelveData.TwelveDataOptions).Assembly;

    protected static IEnumerable<Assembly> AllAdapterAssemblies =>
    [
        TiingoAssembly,
        FrankfurterAssembly,
        FredAssembly,
        BankOfCanadaAssembly,
        BankOfEnglandAssembly,
        EcbAssembly,
        NewYorkFedAssembly,
        UsTreasuryAssembly,
        FamaFrenchAssembly,
        CmeAssembly,
        TwelveDataAssembly,
    ];

    protected static IEnumerable<Assembly> AllAssemblies =>
    [
        SharedAssembly,
        .. AllAdapterAssemblies,
    ];

    protected static string GetFailingTypes(TestResult result) =>
        result.FailingTypes != null
            ? string.Join(", ", result.FailingTypes.Select(t => t.FullName))
            : string.Empty;
}
