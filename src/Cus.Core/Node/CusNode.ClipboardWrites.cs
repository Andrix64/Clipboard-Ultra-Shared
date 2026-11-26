using Cus.Core.Abstractions;

namespace Cus.Core.Node;

public sealed partial class CusNode
{
    /// <summary>Accoda una copia ricevuta per la clipboard. Sotto _gate: l'ordine della coda è quello delle sequenze.</summary>
    private void QueueClipboardWrite(ClipboardWrite write) => _clipboardWrites.Writer.TryWrite(write);

    private async Task ProcessClipboardWritesAsync()
    {
        try
        {
            await foreach (var write in _clipboardWrites.Reader.ReadAllAsync(_lifetime.Token))
            {
                // Superata da una copia più recente, ricevuta (in coda dopo questa) o fatta su questo PC:
                // scriverla ora lascerebbe questo PC diverso dagli altri partecipanti.
                if (Interlocked.Read(ref _clipboardStamp) != write.Stamp)
                    continue;
                try
                {
                    await _writer.WriteAsync(write.Content, write.Files);
                    SetCurrentClipboard(write.Summary);
                    WarnAboutExecutables(write.Content, write.OriginName);
                    if (write.ReceivedLog is { } log)
                        Log(log);
                }
                catch (Exception ex)
                {
                    _notifier.Error(Loc.T("session.canTWriteToTheClipboard", ex.Message));
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <param name="Stamp">Valore di _clipboardStamp assegnato alla copia: se nel frattempo è cambiato, la copia è superata.</param>
    /// <param name="ReceivedLog">Riga del registro attività da scrivere a scrittura riuscita.</param>
    private sealed record ClipboardWrite(
        long Stamp, ClipboardContent Content, IRemoteFileSource? Files, ClipboardSummary Summary, string OriginName, string? ReceivedLog);
}
