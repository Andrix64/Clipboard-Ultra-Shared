using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Cus.Core.Abstractions;

namespace Cus.Platform.Windows;

/// <summary>
/// IStream di sola lettura sopra un file remoto, con letture anticipate da 1 MB per non fare
/// un giro di rete a ogni piccola lettura di Esplora risorse.
/// </summary>
[ComVisible(true)]
internal sealed class RemoteFileStream(IRemoteFileSource source, int fileIndex, long size, string name) : IStream
{
    private const int ChunkSize = 1024 * 1024;
    private const int E_FAIL = unchecked((int)0x80004005);
    private const int STG_E_INVALIDFUNCTION = unchecked((int)0x80030001);

    private long _position;
    private byte[] _buffer = [];
    private long _bufferStart;

    public void Read(byte[] pv, int cb, nint pcbRead)
    {
        var total = 0;
        while (total < cb && _position < size)
        {
            if (_position < _bufferStart || _position >= _bufferStart + _buffer.Length)
                Fill();
            // Mai oltre la dimensione dichiarata, anche se il PC d'origine mandasse più byte.
            var available = (int)Math.Min(Math.Min(cb - total, _bufferStart + _buffer.Length - _position), size - _position);
            if (available <= 0)
                break;
            Array.Copy(_buffer, _position - _bufferStart, pv, total, available);
            total += available;
            _position += available;
        }

        if (pcbRead != 0)
            Marshal.WriteInt32(pcbRead, total);
    }

    public void Seek(long dlibMove, int dwOrigin, nint plibNewPosition)
    {
        var target = dwOrigin switch
        {
            0 => dlibMove,
            1 => _position + dlibMove,
            2 => size + dlibMove,
            _ => throw new COMException(Loc.T("clipboard.invalidOrigin"), STG_E_INVALIDFUNCTION),
        };
        if (target < 0)
            throw new COMException(Loc.T("clipboard.invalidPosition"), STG_E_INVALIDFUNCTION);
        _position = target;
        if (plibNewPosition != 0)
            Marshal.WriteInt64(plibNewPosition, _position);
    }

    public void Stat(out STATSTG pstatstg, int grfStatFlag)
    {
        pstatstg = new STATSTG
        {
            type = 2, // STGTY_STREAM
            cbSize = size,
            pwcsName = (grfStatFlag & 1) != 0 ? null! : Path.GetFileName(name.Replace('\\', '/')),
        };
    }

    public void Write(byte[] pv, int cb, nint pcbWritten) => throw new COMException(Loc.T("clipboard.readOnly"), STG_E_INVALIDFUNCTION);

    public void SetSize(long libNewSize) => throw new COMException(Loc.T("clipboard.readOnly"), STG_E_INVALIDFUNCTION);

    /// <summary>Alcuni programmi in cui si incolla copiano lo stream con CopyTo invece di Read.</summary>
    public void CopyTo(IStream pstm, long cb, nint pcbRead, nint pcbWritten)
    {
        var buffer = new byte[64 * 1024];
        long read = 0, written = 0;
        var count = Marshal.AllocCoTaskMem(sizeof(int));
        try
        {
            while (read < cb)
            {
                Read(buffer, (int)Math.Min(buffer.Length, cb - read), count);
                var got = Marshal.ReadInt32(count);
                if (got <= 0)
                    break;
                read += got;
                Marshal.WriteInt32(count, 0);
                pstm.Write(buffer, got, count);
                written += Marshal.ReadInt32(count);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(count);
        }

        if (pcbRead != 0)
            Marshal.WriteInt64(pcbRead, read);
        if (pcbWritten != 0)
            Marshal.WriteInt64(pcbWritten, written);
    }

    public void Commit(int grfCommitFlags)
    {
    }

    public void Revert()
    {
    }

    public void LockRegion(long libOffset, long cb, int dwLockType) => throw new COMException(Loc.T("clipboard.notSupported"), STG_E_INVALIDFUNCTION);

    public void UnlockRegion(long libOffset, long cb, int dwLockType) => throw new COMException(Loc.T("clipboard.notSupported"), STG_E_INVALIDFUNCTION);

    public void Clone(out IStream ppstm) => ppstm = new RemoteFileStream(source, fileIndex, size, name) { _position = _position };

    private void Fill()
    {
        var count = (int)Math.Min(ChunkSize, size - _position);
        try
        {
            // Esplora risorse chiama da un thread COM: il blocco qui è voluto, le letture di rete
            // girano sul thread pool e non dipendono da questo thread.
            var data = Task.Run(() => source.ReadAsync(fileIndex, _position, count)).GetAwaiter().GetResult();
            if (data.Length == 0)
                throw new RemoteFileUnavailableException(Loc.T("common.theSourcePcReturnedAnEmpty"));
            // Un blocco più lungo del richiesto si tronca: i byte in più non appartengono al file.
            _buffer = data.Length > count ? data[..count] : data;
            _bufferStart = _position;
        }
        catch (Exception ex)
        {
            throw new COMException(ex.Message, E_FAIL);
        }
    }
}
