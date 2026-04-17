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

using Boutquin.MarketData.Abstractions.Contracts;

namespace Boutquin.MarketData.Adapter.Tests.Shared;

/// <summary>
/// In-memory raw document store that tracks all save/load operations.
/// </summary>
internal sealed class FakeRawDocumentStore : IRawDocumentStore
{
    private readonly Dictionary<string, string> _store = new();

    public IReadOnlyDictionary<string, string> Store => _store;

    public List<string> SavedKeys { get; } = [];

    public Task SaveAsync(string key, string content, CancellationToken cancellationToken = default)
    {
        _store[key] = content;
        SavedKeys.Add(key);
        return Task.CompletedTask;
    }

    public Task<string?> LoadAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(_store.TryGetValue(key, out var value) ? value : null);
}
