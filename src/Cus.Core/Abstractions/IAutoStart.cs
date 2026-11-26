namespace Cus.Core.Abstractions;

/// <summary>Avvio automatico di CUS all'accesso dell'utente al sistema operativo.</summary>
public interface IAutoStart
{
    /// <summary>Registra (o aggiorna, se il programma è stato spostato) l'avvio automatico.</summary>
    void Enable();

    /// <summary>Rimuove l'avvio automatico; nessun errore se non c'era.</summary>
    void Disable();
}
