namespace Cus.Core.Abstractions;

/// <summary>Percorsi dei dati dell'istanza. Ogni profilo ha i propri.</summary>
public interface IAppPaths
{
    string DataDirectory { get; }

    string DatabasePath { get; }
}
