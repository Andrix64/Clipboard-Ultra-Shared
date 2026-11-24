using System.Collections.Concurrent;
using Cus.Core.Abstractions;

namespace Cus.Platform.Simulated;

/// <summary>Segreti in memoria, solo per i test: non sopravvivono al riavvio e non sono protetti.</summary>
public sealed class InMemorySecretStore : ISecretStore
{
    private readonly ConcurrentDictionary<string, byte[]> _secrets = new();

    public byte[]? Load(string name) =>
        _secrets.TryGetValue(name, out var secret) ? (byte[])secret.Clone() : null;

    public void Save(string name, byte[] secret) => _secrets[name] = (byte[])secret.Clone();
}
