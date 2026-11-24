namespace Cus.Core.Abstractions;

/// <summary>
/// Conserva piccoli segreti (per esempio la chiave del DB) protetti dal sistema operativo:
/// DPAPI su Windows, Secret Service su Linux. Mai in chiaro accanto al DB.
/// </summary>
public interface ISecretStore
{
    byte[]? Load(string name);

    void Save(string name, byte[] secret);
}
