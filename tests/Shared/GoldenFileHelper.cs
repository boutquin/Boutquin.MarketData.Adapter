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

namespace Boutquin.MarketData.Adapter.Tests.Shared;

/// <summary>
/// Loads embedded resource golden files by convention path.
/// </summary>
internal static class GoldenFileHelper
{
    /// <summary>
    /// Load an embedded resource as a string.
    /// Path is dot-separated from the testdata root (e.g., "tiingo.daily-aapl.json").
    /// </summary>
    public static string LoadText(string relativePath)
    {
        var assembly = Assembly.GetCallingAssembly();
        // Embedded resource names use dots for path separators and hyphens are preserved
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(relativePath.Replace('/', '.'), StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException($"Golden file not found: {relativePath}");

        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Load an embedded resource as a byte array (for ZIP files etc).
    /// </summary>
    public static byte[] LoadBytes(string relativePath)
    {
        var assembly = Assembly.GetCallingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(relativePath.Replace('/', '.'), StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException($"Golden file not found: {relativePath}");

        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }
}
