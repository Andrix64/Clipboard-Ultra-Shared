using Cus.Core.Abstractions;
using Cus.Core.Identity;
using Cus.Core.Storage;

namespace Cus.Core.Trust;

/// <summary>Dispositivo dello stesso utente secondo la lista di fiducia.</summary>
public sealed record TrustedDevice(string DeviceId, string DisplayName, byte[] SignPublicKey);

/// <summary>Stato calcolato dalla lista: deterministico, non dipende dall'ordine di arrivo delle voci.</summary>
/// <param name="Group">
/// Tutti i dispositivi raggiungibili tramite abbinamenti, revocati compresi: le loro voci
/// contano nel calcolo, quindi le si accetta da loro (anche per consegnare revoche reciproche).
/// </param>
public sealed record TrustState(
    IReadOnlyDictionary<string, TrustedDevice> Trusted,
    IReadOnlySet<string> Revoked,
    bool SelfRevoked,
    IReadOnlySet<string> Group)
{
    public bool IsGroupMember(string deviceId) => Group.Contains(deviceId);

    public bool IsTrusted(string deviceId) => Trusted.ContainsKey(deviceId);

    public bool IsRevoked(string deviceId) => Revoked.Contains(deviceId);
}

/// <summary>
/// Lista firmata di abbinamenti e revoche (vedi IDEA.md, "Lista di revoche firmata").
/// <para>Regole di calcolo, applicate all'insieme delle voci con firma valida:</para>
/// <list type="number">
/// <item>C = chiusura degli abbinamenti partendo da questo dispositivo (A abbina B, B abbina C...);</item>
/// <item>una revoca vale se il suo attore è in C; i soggetti revocati formano R (anche revoche reciproche, anche auto-revoche);</item>
/// <item>i dispositivi fidati sono la chiusura degli abbinamenti fatti da attori non revocati, meno R.</item>
/// </list>
/// Il calcolo dipende solo dall'insieme delle voci. All'ingresso però <see cref="Merge"/> scarta le
/// voci nuove firmate da dispositivi già revocati qui (vedi lì il perché).
/// </summary>
public sealed class TrustLog
{
    public const int MaxEntries = 10_000;

    private readonly CusDatabase _database;
    private readonly DeviceIdentity _self;
    private readonly Lock _gate = new();
    private readonly List<TrustEntry> _entries;
    private readonly HashSet<string> _known;

    public TrustLog(CusDatabase database, DeviceIdentity self)
    {
        _database = database;
        _self = self;
        _entries = database.GetTrustEntries().Where(e => e.HasValidSignature()).ToList();
        _known = _entries.Select(e => e.EntryId).ToHashSet();
        State = ComputeVerified(self.SignPublicKey, _entries);
    }

    public event EventHandler? Changed;

    public TrustState State { get; private set; }

    public IReadOnlyList<TrustEntry> Entries
    {
        get { lock (_gate) return [.. _entries]; }
    }

    /// <summary>Dispositivi che <paramref name="deviceId"/> ha abbinato: da riesaminare se viene revocato.</summary>
    public IReadOnlyList<TrustedDevice> PairedBy(string deviceId)
    {
        lock (_gate)
        {
            return _entries
                .Where(e => e.Kind == TrustEntryKind.Pair && e.ActorId == deviceId && e.SubjectId != _self.DeviceId)
                .GroupBy(e => e.SubjectId)
                .Select(g => g.OrderBy(e => e.CreatedUtc).Last())
                .Select(e => new TrustedDevice(e.SubjectId, DisplayText.Sanitize(e.SubjectName, 64), e.SubjectPublicKey))
                .ToList();
        }
    }

    public TrustEntry Pair(DeviceInfo subject) => AddOwn(TrustEntryKind.Pair, subject);

    public TrustEntry Revoke(DeviceInfo subject) => AddOwn(TrustEntryKind.Revoke, subject);

    /// <summary>Lunghezza massima del nome del soggetto in una voce (i nomi locali sono al massimo 64 caratteri).</summary>
    public const int MaxSubjectNameLength = 256;

    /// <summary>Unisce voci ricevute da un altro dispositivo dell'utente.</summary>
    /// <remarks>
    /// Regola 1 di IDEA.md ("una revoca è valida solo se chi la firma non era già revocato"): le
    /// date delle voci le sceglie chi firma, quindi non provano nulla. Fa fede ciò che questo PC sa
    /// già: una voce nuova firmata da un dispositivo che qui risulta revocato si scarta, salvo la
    /// revoca di chi lo ha revocato (regola 4, revoca reciproca). Senza questo un dispositivo
    /// revocato potrebbe revocare tutti gli altri, o abbinare un'identità nuova e revocare tramite lei.
    /// Le voci accettate restano: l'insieme cresce soltanto.
    /// </remarks>
    /// <returns>True se è cambiato qualcosa.</returns>
    public bool Merge(IEnumerable<TrustEntry> incoming)
    {
        var added = false;
        lock (_gate)
        {
            // Ordine stabile (data, poi ID): voci dello stesso lotto valutate sempre nello stesso modo.
            var candidates = incoming
                .Where(IsWellFormed)
                .Select(e => (Entry: e, Id: e.EntryId))
                .Where(c => !_known.Contains(c.Id))
                .DistinctBy(c => c.Id)
                .OrderBy(c => c.Entry.CreatedUtc)
                .ThenBy(c => c.Id, StringComparer.Ordinal)
                .ToList();

            foreach (var (entry, id) in candidates)
            {
                if (_entries.Count >= MaxEntries)
                    break;
                if (!entry.HasValidSignature() || IsFromRevokedActor(entry))
                    continue;
                _known.Add(id);
                _database.AddTrustEntry(entry);
                _entries.Add(entry);
                added = true;

                // Una revoca cambia chi è revocato: le voci successive del lotto vanno giudicate col nuovo stato.
                if (entry.Kind == TrustEntryKind.Revoke)
                    State = ComputeVerified(_self.SignPublicKey, _entries);
            }

            if (added)
                State = ComputeVerified(_self.SignPublicKey, _entries);
        }

        if (added)
            Changed?.Invoke(this, EventArgs.Empty);
        return added;
    }

    private static bool IsWellFormed(TrustEntry? entry) =>
        entry is { ActorPublicKey.Length: 32, SubjectPublicKey.Length: 32, SubjectName: not null, Signature.Length: 64 }
        && Enum.IsDefined(entry.Kind)
        && entry.SubjectName.Length <= MaxSubjectNameLength;

    /// <summary>Voce nuova di un attore già revocato qui: valida solo se revoca chi ha revocato lui.</summary>
    private bool IsFromRevokedActor(TrustEntry entry)
    {
        var actor = entry.ActorId;
        if (!State.IsRevoked(actor))
            return false;
        if (entry.Kind != TrustEntryKind.Revoke)
            return true;
        var subject = entry.SubjectId;
        return !_entries.Any(e => e.Kind == TrustEntryKind.Revoke && e.SubjectId == actor && e.ActorId == subject);
    }

    public static TrustState Compute(byte[] selfPublicKey, IReadOnlyCollection<TrustEntry> entries) =>
        ComputeVerified(selfPublicKey, entries.Where(e => e.HasValidSignature()).ToList());

    /// <summary>Come <see cref="Compute"/>, per voci la cui firma è già stata verificata (all'ingresso nella lista).</summary>
    private static TrustState ComputeVerified(byte[] selfPublicKey, IReadOnlyList<TrustEntry> valid)
    {
        var selfId = DeviceIdentity.ComputeDeviceId(selfPublicKey);
        var pairs = valid.Where(e => e.Kind == TrustEntryKind.Pair).ToList();

        var reachable = Closure(selfId, pairs, excludedActors: []);
        var revoked = valid
            .Where(e => e.Kind == TrustEntryKind.Revoke && reachable.Contains(e.ActorId))
            .Select(e => e.SubjectId)
            .ToHashSet();

        if (revoked.Contains(selfId))
            return new TrustState(new Dictionary<string, TrustedDevice>(), revoked, SelfRevoked: true, reachable);

        var trustedIds = Closure(selfId, pairs, revoked);
        trustedIds.ExceptWith(revoked);
        trustedIds.Remove(selfId);

        var trusted = pairs
            .Where(e => trustedIds.Contains(e.SubjectId))
            .GroupBy(e => e.SubjectId)
            .Select(g => g.OrderBy(e => e.CreatedUtc).Last())
            .ToDictionary(e => e.SubjectId, e => new TrustedDevice(e.SubjectId, DisplayText.Sanitize(e.SubjectName, 64), e.SubjectPublicKey));

        return new TrustState(trusted, revoked, SelfRevoked: false, reachable);
    }

    /// <summary>Visita in ampiezza del grafo attore → soggetto: lineare nel numero di voci.</summary>
    private static HashSet<string> Closure(string selfId, List<TrustEntry> pairs, HashSet<string> excludedActors)
    {
        var edges = new Dictionary<string, List<string>>();
        foreach (var pair in pairs)
        {
            if (!edges.TryGetValue(pair.ActorId, out var subjects))
                edges[pair.ActorId] = subjects = [];
            subjects.Add(pair.SubjectId);
        }

        var result = new HashSet<string> { selfId };
        var queue = new Queue<string>([selfId]);
        while (queue.TryDequeue(out var actor))
        {
            if (excludedActors.Contains(actor) || !edges.TryGetValue(actor, out var subjects))
                continue;
            foreach (var subject in subjects)
            {
                if (result.Add(subject))
                    queue.Enqueue(subject);
            }
        }

        return result;
    }

    private TrustEntry AddOwn(TrustEntryKind kind, DeviceInfo subject)
    {
        var entry = TrustEntry.Create(_self, kind, subject, DateTimeOffset.UtcNow);
        Merge([entry]);
        return entry;
    }
}
