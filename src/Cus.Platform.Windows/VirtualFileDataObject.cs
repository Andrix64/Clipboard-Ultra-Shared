using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Cus.Core.Abstractions;
using Cus.Platform.Windows.Interop;
using ComIDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace Cus.Platform.Windows;

/// <summary>
/// IDataObject con "virtual files" (FileGroupDescriptorW + FileContents): Esplora risorse
/// incolla i file leggendo uno stream che scarica i byte dal PC d'origine solo in quel momento.
/// </summary>
[ComVisible(true)]
internal sealed class VirtualFileDataObject : ComIDataObject
{
    private const int S_OK = 0;
    private const int DV_E_FORMATETC = unchecked((int)0x80040064);
    private const int DATA_S_SAMEFORMATETC = 0x00040130;
    private const int OLE_E_ADVISENOTSUPPORTED = unchecked((int)0x80040003);
    private const int E_NOTIMPL = unchecked((int)0x80004001);

    private const uint FD_ATTRIBUTES = 0x00000004;
    private const uint FD_FILESIZE = 0x00000040;
    private const uint FD_PROGRESSUI = 0x00004000;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    private const int FileDescriptorSize = 592;

    /// <summary>URLZONE_INTERNET.</summary>
    private const uint UrlZoneInternet = 3;

    private readonly IReadOnlyList<Entry> _entries;
    private readonly IRemoteFileSource _source;
    private readonly ClipboardFormats _formats;

    /// <exception cref="ArgumentException">
    /// Elenco non valido (vedi <see cref="ClipboardFileNames.TryValidate"/>): nomi con '\', unità,
    /// percorsi UNC, flussi alternativi, nomi di dispositivo o '..' scriverebbero fuori dalla
    /// cartella in cui l'utente incolla. Si rifiuta tutto l'elenco, mai un nome corretto in silenzio.
    /// </exception>
    public VirtualFileDataObject(IReadOnlyList<ClipboardFileInfo> files, IRemoteFileSource source, ClipboardFormats formats)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(formats);
        if (!ClipboardFileNames.TryValidate(files, out var reason))
            throw new ArgumentException($"Elenco di file ricevuto non valido: {reason}.", nameof(files));
        _source = source;
        _formats = formats;
        _entries = BuildEntries(files);
    }

    internal IReadOnlyList<Entry> Entries => _entries;

    public void GetData(ref FORMATETC format, out STGMEDIUM medium)
    {
        medium = default;
        var cf = (ushort)format.cfFormat;

        if (cf == _formats.FileGroupDescriptor && (format.tymed & TYMED.TYMED_HGLOBAL) != 0)
        {
            medium = HGlobal(BuildDescriptor());
            return;
        }

        if (cf == _formats.FileContents && format.lindex >= 0 && format.lindex < _entries.Count
            && _entries[format.lindex].FileIndex >= 0 && (format.tymed & TYMED.TYMED_ISTREAM) != 0)
        {
            var entry = _entries[format.lindex];
            var stream = new RemoteFileStream(_source, entry.FileIndex, entry.Size, entry.Name);
            medium = new STGMEDIUM
            {
                tymed = TYMED.TYMED_ISTREAM,
                unionmember = Marshal.GetComInterfaceForObject<RemoteFileStream, IStream>(stream),
            };
            return;
        }

        if (cf == _formats.PreferredDropEffect && (format.tymed & TYMED.TYMED_HGLOBAL) != 0)
        {
            medium = HGlobal(BitConverter.GetBytes(1)); // DROPEFFECT_COPY
            return;
        }

        if (cf == _formats.OwnWriteMarker && (format.tymed & TYMED.TYMED_HGLOBAL) != 0)
        {
            medium = HGlobal([1]);
            return;
        }

        // File arrivati da un altro PC: zona Internet. Esplora risorse aggiunge il Mark-of-the-Web, e un
        // eseguibile ricevuto passa dal controllo di SmartScreen come uno scaricato.
        if (cf == _formats.ZoneIdentifier && (format.tymed & TYMED.TYMED_HGLOBAL) != 0)
        {
            medium = HGlobal(BitConverter.GetBytes(UrlZoneInternet));
            return;
        }

        // DWORD 0: i file ricevuti non finiscono nella cronologia Win+V né nella clipboard cloud.
        if ((cf == _formats.CanIncludeInHistory || cf == _formats.CanUploadToCloud) && (format.tymed & TYMED.TYMED_HGLOBAL) != 0)
        {
            medium = HGlobal(BitConverter.GetBytes(0u));
            return;
        }

        throw new COMException("Formato non disponibile.", DV_E_FORMATETC);
    }

    public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) =>
        throw new COMException("Non supportato.", E_NOTIMPL);

    public int QueryGetData(ref FORMATETC format)
    {
        var cf = (ushort)format.cfFormat;
        if (cf == _formats.FileContents)
            return (format.tymed & TYMED.TYMED_ISTREAM) != 0 ? S_OK : DV_E_FORMATETC;
        return SupportedHGlobalFormats().Contains(cf) && (format.tymed & TYMED.TYMED_HGLOBAL) != 0 ? S_OK : DV_E_FORMATETC;
    }

    public int GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut)
    {
        formatOut = formatIn;
        formatOut.ptd = 0;
        return DATA_S_SAMEFORMATETC;
    }

    public void SetData(ref FORMATETC formatIn, ref STGMEDIUM medium, bool release)
    {
        // Esplora risorse comunica "Performed DropEffect" / "Paste Succeeded": li ignoriamo.
        if (release)
            ReleaseStgMedium(ref medium);
    }

    public IEnumFORMATETC EnumFormatEtc(DATADIR direction)
    {
        if (direction != DATADIR.DATADIR_GET)
            throw new COMException("Non supportato.", E_NOTIMPL);

        var formats = SupportedHGlobalFormats()
            .Select(cf => new FORMATETC { cfFormat = (short)cf, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL })
            .Append(new FORMATETC { cfFormat = (short)_formats.FileContents, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_ISTREAM })
            .ToArray();
        return new FormatEnumerator(formats);
    }

    public int DAdvise(ref FORMATETC format, ADVF advf, IAdviseSink adviseSink, out int connection)
    {
        connection = 0;
        return OLE_E_ADVISENOTSUPPORTED;
    }

    public void DUnadvise(int connection) => throw new COMException("Non supportato.", OLE_E_ADVISENOTSUPPORTED);

    public int EnumDAdvise(out IEnumSTATDATA? enumAdvise)
    {
        enumAdvise = null;
        return OLE_E_ADVISENOTSUPPORTED;
    }

    /// <summary>Descrittore FILEGROUPDESCRIPTORW: conteggio + un FILEDESCRIPTORW da 592 byte per voce.</summary>
    internal byte[] BuildDescriptor()
    {
        var buffer = new byte[4 + _entries.Count * FileDescriptorSize];
        BitConverter.TryWriteBytes(buffer.AsSpan(0, 4), _entries.Count);
        for (var i = 0; i < _entries.Count; i++)
        {
            var entry = _entries[i];
            var offset = 4 + i * FileDescriptorSize;
            var isFile = entry.FileIndex >= 0;
            BitConverter.TryWriteBytes(buffer.AsSpan(offset, 4), FD_ATTRIBUTES | (isFile ? FD_FILESIZE | FD_PROGRESSUI : 0));
            BitConverter.TryWriteBytes(buffer.AsSpan(offset + 36, 4), isFile ? FILE_ATTRIBUTE_NORMAL : FILE_ATTRIBUTE_DIRECTORY);
            BitConverter.TryWriteBytes(buffer.AsSpan(offset + 64, 4), (uint)(entry.Size >> 32));
            BitConverter.TryWriteBytes(buffer.AsSpan(offset + 68, 4), (uint)(entry.Size & 0xFFFFFFFF));
            // cFileName è WCHAR[260] con il terminatore: i nomi sono già validati entro 259 caratteri.
            // Un nome più lungo è un errore e non si tronca (cambierebbe il file creato).
            if (entry.Name.Length > ClipboardFileNames.MaxPathLength)
                throw new InvalidOperationException($"Nome troppo lungo per il descrittore: {entry.Name.Length} caratteri.");
            Encoding.Unicode.GetBytes(entry.Name, buffer.AsSpan(offset + 72, 520));
        }

        return buffer;
    }

    private static IReadOnlyList<Entry> BuildEntries(IReadOnlyList<ClipboardFileInfo> files)
    {
        var entries = new List<Entry>();
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < files.Count; i++)
        {
            // Nomi già validati: segmenti non vuoti separati da '/', senza '\'. Le cartelle
            // intermedie sono i prefissi fino a ogni separatore, in ordine di profondità.
            var path = files[i].Name.Replace('/', '\\');
            for (var separator = path.IndexOf('\\'); separator >= 0; separator = path.IndexOf('\\', separator + 1))
            {
                var directory = path[..separator];
                if (directories.Add(directory))
                    entries.Add(new Entry(directory, 0, -1));
            }

            entries.Add(new Entry(path, files[i].SizeBytes, i));
        }

        return entries;
    }

    private ushort[] SupportedHGlobalFormats() =>
        [_formats.FileGroupDescriptor, _formats.PreferredDropEffect, _formats.OwnWriteMarker, _formats.CanIncludeInHistory, _formats.CanUploadToCloud, _formats.ZoneIdentifier];

    private static STGMEDIUM HGlobal(ReadOnlySpan<byte> data) => new()
    {
        tymed = TYMED.TYMED_HGLOBAL,
        unionmember = Native.ToHGlobal(data),
    };

    [DllImport("ole32.dll")]
    private static extern void ReleaseStgMedium(ref STGMEDIUM medium);

    /// <param name="FileIndex">Indice nell'elenco originale dei file; -1 per le cartelle.</param>
    internal sealed record Entry(string Name, long Size, int FileIndex);

    [ComVisible(true)]
    private sealed class FormatEnumerator(FORMATETC[] formats) : IEnumFORMATETC
    {
        private int _position;

        public int Next(int celt, FORMATETC[] rgelt, int[]? pceltFetched)
        {
            var fetched = 0;
            while (fetched < celt && _position < formats.Length)
                rgelt[fetched++] = formats[_position++];
            if (pceltFetched is { Length: > 0 })
                pceltFetched[0] = fetched;
            return fetched == celt ? 0 : 1;
        }

        public int Skip(int celt)
        {
            _position = Math.Min(formats.Length, _position + celt);
            return _position < formats.Length ? 0 : 1;
        }

        public int Reset()
        {
            _position = 0;
            return 0;
        }

        public void Clone(out IEnumFORMATETC newEnum) => newEnum = new FormatEnumerator(formats) { _position = _position };
    }
}

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
            _ => throw new COMException("Origine non valida.", STG_E_INVALIDFUNCTION),
        };
        if (target < 0)
            throw new COMException("Posizione non valida.", STG_E_INVALIDFUNCTION);
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

    public void Write(byte[] pv, int cb, nint pcbWritten) => throw new COMException("Sola lettura.", STG_E_INVALIDFUNCTION);

    public void SetSize(long libNewSize) => throw new COMException("Sola lettura.", STG_E_INVALIDFUNCTION);

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

    public void LockRegion(long libOffset, long cb, int dwLockType) => throw new COMException("Non supportato.", STG_E_INVALIDFUNCTION);

    public void UnlockRegion(long libOffset, long cb, int dwLockType) => throw new COMException("Non supportato.", STG_E_INVALIDFUNCTION);

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
                throw new RemoteFileUnavailableException("Il PC d'origine ha restituito un blocco vuoto.");
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
