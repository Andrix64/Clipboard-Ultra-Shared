# CUS — Piano di sviluppo

> Creato il 2026-11-24. Copre solo lo sviluppo, fino a un'applicazione Windows completa e provata. Restano fuori installer, firma del codice, distribuzione, aggiornamenti automatici e porting a Linux. Requisiti e decisioni sono in [IDEA.md](IDEA.md).

## Stato al 2026-11-25

Ridisegno del modello (vedi IDEA.md, "Modello del 2026-11-25"). `dotnet build Cus.slnx` senza warning; `dotnet test`: 139 + 74 + 57 = 270 (dopo la revisione e le questioni aperte qui sotto). I 57 test Windows sono da rieseguire a schermo sbloccato: usano la clipboard reale, che con lo schermo bloccato non risponde. Copertura delle righe di `Cus.Core` e delle piattaforme circa 85% (coverlet); `Cus.UI` e `Cus.App.Windows` non hanno test automatici.

| Progetto di test | Test | Copre |
|---|---|---|
| `Cus.Core.Tests` | 123 | opzioni, profili, DB cifrato e migrazione 3 → 4, PNG, canale cifrato, nomi dei file, amici, riepilogo della clipboard, nomi accorciati e dimensioni, pulizia dei nomi dalla rete |
| `Cus.IntegrationTests` | 70 | visibilità e richieste di ingresso (attesa dopo un rifiuto), permessi per membro, patti, proposte concorrenti o senza risposta e scioglimento (anche a PC spento), rimozione di un amico, password e sessione nuova dopo l'ultimo membro, trasferimenti che sopravvivono a una nuova copia e annullamento, scoperta con conteggio, peer manuali, messaggi ostili |
| `Cus.Platform.Windows.Tests` | 57 | DPAPI, DIB, clipboard reale, contenuti sensibili, virtual files, incolla vero con Esplora risorse |

Cosa è cambiato nel codice:

- **Tolti:** `Crypto/CPace.cs`, i binding ristretto255, `Trust/` (lista di revoche), `Node/CusNode.Pairing.cs` e i loro test (39). La migrazione 4 del DB elimina `trust_entries` e gli indirizzi dei PC abbinati e crea `friends`.
- **Protocollo:** il secondo messaggio di ogni connessione è `FriendSyncMessage` (livello del patto, si tiene il più basso). Al posto dell'invito dell'host ci sono `JoinRequestMessage` e `JoinReplyMessage`; nuovi `SetDirectionMessage`, `AgreementProposalMessage` e `AgreementReplyMessage`. `ClipboardPushMessage` e `ClipboardItemMessage` portano il flag `Sensitive`; la lettura dei file porta il richiedente vero quando l'host la inoltra.
- **Nucleo:** `CusNode.Sessions.cs` (visibilità, ingresso, permessi, instradamento delle password), `CusNode.Friends.cs` (patti, fiducia), `CusNode.Transfers.cs` (offerte e trasferimenti), `Friends/FriendStore.cs`.
- **UI:** `MainWindow` a tre corpi, `AvaloniaOverlayIndicator` aggiornato sul posto, `TransfersWindow`, icona nell'area di notifica in `App.axaml.cs`.
- **Verifica a mano (2026-11-25):** due istanze sullo stesso PC pilotate con UI Automation: offerta come host, richiesta di ingresso, consenso, copia condivisa con "testo di N caratteri", indicatore reale con un file dal nome lungo accorciato in mezzo, passaggio a sola lettura dall'indicatore.

## Stato al 2026-11-24

Fasi 0–4 completate. `dotnet build` senza warning; `dotnet test` 228 su 228 (dopo la modifica della sola lettura):

| Progetto di test | Test | Copre |
|---|---|---|
| `Cus.Core.Tests` | 134 | opzioni, profili, DB cifrato e migrazioni, PNG, CPace (test vector B.3), canale cifrato, lista di fiducia, nomi dei file ricevuti |
| `Cus.IntegrationTests` | 37 | più nodi in loopback: sessioni, gruppi, sola lettura, convergenza, file lazy, cronologia, abbinamento, revoche, scoperta, messaggi ostili |
| `Cus.Platform.Windows.Tests` | 57 | DPAPI, DIB, clipboard reale, contenuti sensibili, virtual files, incolla vero con Esplora risorse |

Verifiche manuali sullo stesso PC, fatte il 2026-11-24:

- due istanze simulate: invito, consenso, copie nei due sensi, cronologia;
- istanza `real` contro `simulated`: clipboard di Windows nei due sensi, indicatore su tutti e 3 i monitor, clic sull'indicatore che chiude la sessione senza rubare il focus;
- file da 1 GB copiato su un nodo e incollato da Esplora risorse sull'altro: hash identico, circa 14 s in loopback (`CUS_BIG_FILE_MB=1024`).

### Scostamenti dal piano

- **Trasporto:** al posto di TLS con certificati autofirmati c'è un canale costruito con libsodium (`Cus.Core/Net/SecureChannel.cs`):
  - chiavi effimere X25519 firmate con l'identità Ed25519;
  - chiavi di sessione legate alla trascrizione;
  - secretstream XChaCha20-Poly1305 in ogni direzione.

  Motivo: è la stessa identità usata da abbinamento e revoche, senza gestire certificati, e funziona uguale su Linux. IDEA.md lo prevedeva come alternativa ("un protocollo tipo Noise").
- **Scoperta:** annunci UDP multicast (239.255.23.70:23701) invece di mDNS/DNS-SD. Sono più semplici e permettono più istanze sullo stesso PC. Gli annunci sono firmati e numerati (vedi sotto, sicurezza); l'identità completa la verifica comunque il canale. Gli annunci dallo stesso PC vengono mappati su 127.0.0.1.
- **Ordine delle copie:**
  - le copie locali si elaborano in coda, una alla volta;
  - l'host conferma ogni copia ricevuta (`ClipboardAckMessage`), e un membro non applica copie altrui finché la propria non è confermata;
  - l'host scarta una propria copia superata da una remota già scritta.

  Senza queste regole, con copie quasi simultanee i PC potevano restare diversi tra loro.
- **Indirizzi dei PC abbinati:** si memorizzano (porta di ascolto inclusa nel saluto) per consegnare revoche e abbinamenti anche senza scoperta automatica.
- **Indicatore:** finestre Avalonia, una per monitor, rese non attivabili e fuori da Alt+Tab via Win32 (`Cus.App.Windows/NativeWindowStyle.cs`). In modalità simulata non compare, per non sovrapporre gli indicatori di più istanze; lo stato si vede nel display della finestra.
- **Interfaccia:** skin "lettore retrò" (telaio metallico, display LCD lime, pulsanti tondi), definito in `Cus.UI/Skin.cs` e `Cus.UI/SkinStyles.axaml`.
- **Sola lettura scelta da chi invita (2026-11-24):** la modalità la sceglie l'host per ogni invito (pulsante accanto a INVITA), e l'invitato può solo ridurre un invito bidirezionale a sola lettura. In sola lettura il membro riceve tutte le copie della sessione, cronologia e file compresi, ma le sue non escono: il membro non le invia, l'host le ignora se arrivano comunque e il membro rifiuta le letture dei propri file. Prima era il contrario: la sceglieva chi accettava, e il membro inviava senza ricevere.
- **Indurimento dopo la revisione di sicurezza (2026-11-24):**
  - un invito a un peer scelto dall'elenco verifica che il canale appartenga al `DeviceId` annunciato; gli annunci si tengono per ID + indirizzo, così uno falso non sostituisce quello vero;
  - i nomi dei file ricevuti si validano (`Cus.Core/Abstractions/ClipboardFileNames.cs`): niente `..`, `\`, `:`, nomi di dispositivo, oltre 10.000 file o 32 livelli; un elenco non valido si rifiuta per intero;
  - i nomi che arrivano dalla rete passano da `DisplayText.Sanitize` (niente a capo, controllo, bidi);
  - i messaggi oltre `SecureChannel.MaxMessageBytes` si rifiutano prima di cifrarli e la connessione resta valida; il JSON usa l'escaping "rilassato" (gli accenti non diventano `\uXXXX`);
  - frame pre-autenticazione limitati alla dimensione attesa, massimo 16 connessioni in ingresso in fase iniziale, una sola richiesta di consenso alla volta, timeout di 15 s su ogni invio, letture di file servite al massimo 8 alla volta;
  - gli handler dei messaggi girano in un ciclo separato dalla lettura del socket: risposte e ping non aspettano più un handler lento;
  - **cronologia ai membri:** solo le voci della sessione in corso, e come anteprime (`HistoryEntry.Preview`); la voce completa si chiede all'host (`HistoryItemRequestMessage`) quando la si usa. L'host continua a vedere tutte le voci salvate;
  - abbinamento: chi inserisce il codice verifica la conferma dell'altro PC prima di mandare la propria.
- **Seconda revisione (2026-11-24):**
  - **revoche:** una voce nuova firmata da un dispositivo che questo PC sa già revocato si scarta, salvo la revoca di chi lo ha revocato (regola 4, revoca reciproca). Applica la regola 1 di IDEA.md: prima un dispositivo revocato poteva revocare tutti gli altri. Le date delle voci le sceglie chi firma, quindi fa fede ciò che il PC sapeva già al momento dell'unione. Conseguenza: due PC possono restare diversi solo se uno ha ricevuto la revoca del dispositivo dopo le sue voci e l'altro prima. Si scartano anche voci con tipo sconosciuto o nome oltre 256 caratteri (`Cus.Core/Trust/TrustLog.cs`);
  - nomi dei file ricevuti: rifiutati anche i caratteri invisibili e di direzione (U+202E e simili) e gli elenchi oltre 20.000 voci tra file e cartelle;
  - sola lettura applicata anche dal membro (le copie dell'host si ignorano) e, sull'host, niente cronologia né letture di file ai membri in sola lettura;
  - `ItemId` delle copie: 24 cifre esadecimali, mai uno già usato (sostituirebbe la voce di un altro PC); l'host ricorda l'origine solo delle ultime 256 copie;
  - connessioni in ingresso: chiuse anche quando la fase iniziale fallisce, al massimo 4 per indirizzo (loopback escluso), lista di fiducia da chi non è nel gruppo fino a 1 MB, primo messaggio fino a 64 KB e 10 s;
  - l'host conferma sempre la copia di un membro, anche se il salvataggio fallisce; una sessione rimasta senza membri si chiude anche nella corsa invito/disconnessione; due inviti contemporanei allo stesso PC non si sovrascrivono;
  - attività in sottofondo del nodo tracciate: errori registrati e arresto che le aspetta (al massimo 5 s) prima di chiudere il database;
  - PNG: rifiutato prima di allocare se i dati compressi non bastano per le dimensioni dichiarate;
  - librerie native cercate solo nella cartella dell'applicazione e in System32;
  - UI: la lista dei peer non sovrascrive più ogni 3 s l'indirizzo di abbinamento, pulsanti disattivati durante inviti/abbinamenti/revoche, nomi e testi dalla rete ripuliti anche nella cronologia, notifiche impilate.

### Revisione di sicurezza e correttezza (2026-11-25)

- **Sessione riusata (bloccante):** con l'host visibile, l'uscita dell'ultimo membro non chiudeva la sessione. Chi entrava dopo ereditava il permesso temporaneo per le password e la cronologia dei membri di prima. Ora parte una sessione nuova (`OnMemberClosedAsync`); test `SensitiveForAll_AndHistory_DoNotPassToWhoJoinsAfterTheLastMemberLeft`.
- **Visibilità:** chi chiede di entrare mentre si offre torna visibile se la richiesta fallisce; durante una richiesta in corso non ci si può offrire. Una sessione creata da un patto e rimasta vuota per un errore d'invio non resta in giro.
- **Richieste insistenti:** dopo un rifiuto, una scadenza o un annullamento, 30 s di attesa per dispositivo e indirizzo (`CusNodeOptions.JoinRetryCooldown`).
- **Letture di file:** al massimo 16 in corso per connessione; lo slot globale (8) copre solo la lettura dal disco, non l'attesa del PC d'origine quando l'host inoltra.
- **Saluto iniziale:** limitato a 4 KB (prima 8 MB per chiunque si collegasse).
- **Nomi dalla rete:** `DisplayText.Sanitize` toglie anche le lettere che si disegnano vuote (riempitivi Hangul, Braille vuoto) e i segni combinanti oltre due per lettera.
- **Amici:** `FriendStore.Remove` sotto lo stesso lock di `RememberEndpoint` (un amico rimosso non torna), e notifica solo se c'era.
- **Trasferimenti:** un pezzo letto due volte non conta doppio e non chiude il trasferimento in anticipo; i conteggi dell'indicatore si aggiornano subito.
- **Questioni aperte risolte (2026-11-25):**
  - **handshake versione 2** (`Cus.Core/Net/SecureChannel.cs`): si scambiano prima solo chiavi effimere e da lì tutto è cifrato; chi si collega manda identità e firma (chiavi effimere più la propria identità), chi risponde la verifica e solo dopo decide se presentarsi (`revealTo`). `CusNode.RevealsIdentityTo`: a tutti se visibile, altrimenti solo agli amici con patto. Le versioni 1 e 2 non si parlano;
  - **nomi falsi:** `DisplayText.Skeleton`/`LooksAlike`, `PeerView.LooksLikeFriend` e `Fingerprint`, `JoinRequestView`/`ConsentRequest.LooksLikeFriend`; conferma prima di entrare in `MainWindow`, avviso nella finestra di consenso, notifica a chi entra;
  - **file ricevuti:** formato `ZoneIdentifier` (CFSTR_ZONEIDENTIFIER, zona Internet) in `VirtualFileDataObject`; `ClipboardFileNames.MaxTotalBytes` (1 TiB) ed `ExecutableNames`; avviso in `CusNode.WarnAboutExecutables`;
  - **invii fuori dal lock:** `MemberOutbox` per membro (64 messaggi, 32 MB), riempita sotto `_gate` da `Post` e svuotata da `PumpAsync`;
  - **annunci firmati:** `Discovery` firma ogni annuncio (busta `SignedAnnouncement`: corpo, chiave, firma Ed25519 con prefisso `CUS-discovery-v2`) e lo numera (`AnnouncementPayload.Sequence`); chi riceve verifica firma e ID = chiave, poi `AnnouncementFreshness` accetta solo contatori crescenti per dispositivo (dimentica dopo 10 minuti, al più 1024 dispositivi).

### Come provare a mano

```powershell
.\scripts\run-two-instances.ps1
```

```powershell
.\scripts\run-two-instances.ps1 -Mode RealAndSimulated
```

## Stato di partenza (fase 0, completata il 2026-11-24)

- **Soluzione:** `Cus.slnx` con .NET 10, gestione centralizzata dei pacchetti (`Directory.Packages.props`) e `nuget.config` con solo nuget.org.
- **Progetti:**

| Progetto | Target | Ruolo |
|---|---|---|
| `src/Cus.Core` | `net10.0` | Nucleo indipendente dalla piattaforma: interfacce (`Abstractions/`), opzioni e profili (`Hosting/`), DB cifrato (`Storage/`), libsodium (`Crypto/`). |
| `src/Cus.Platform.Simulated` | `net10.0` | Clipboard virtuale, indicatore senza finestre, segreti in memoria. Servono per i test e per più istanze sullo stesso PC. |
| `src/Cus.Platform.Windows` | `net10.0-windows10.0.17763.0` | Implementazioni Windows. Ancora vuoto. |
| `src/Cus.UI` | `net10.0` | UI Avalonia. Vede solo le interfacce di `Cus.Core`. |
| `src/Cus.App.Windows` | `net10.0-windows10.0.17763.0` | Eseguibile `cus.exe`: legge le opzioni e collega le implementazioni (composition root). |
| `tests/Cus.Core.Tests` | `net10.0` | Test del nucleo e delle implementazioni simulate. Non dipendono da Windows. |

- **Verificato:** build senza warning; 36 test superati, compresi gli smoke test di SQLite3MC (DB davvero cifrato, chiave sbagliata rifiutata) e di libsodium (funzioni ristretto255 disponibili). La finestra si apre con `--clipboard simulated`.

## Testabilità su un singolo PC

Ogni fase deve essere verificabile con un solo PC Windows. I mezzi, già predisposti in fase 0:

- **Profili:** `--profile <nome>` dà a ogni istanza cartella dati, DB, identità e chiavi propri. Due profili sullo stesso PC si comportano come due dispositivi diversi.
- **Porte:** `--port <n>` assegna a ogni istanza una porta propria. Senza l'opzione si userà la prima libera tra 23700 e 23709.
- **Peer manuali:** `--peer 127.0.0.1:<porta>` collega le istanze in loopback, senza dipendere dalla scoperta in rete.
- **Clipboard simulata:** con `--clipboard simulated` ogni istanza ha una clipboard virtuale, con un campo "Copia" nella finestra. Così due istanze non si contendono la clipboard di Windows e non vanno in loop tra loro.
- **Avvio rapido:** `scripts/run-two-instances.ps1` avvia `alice` e `bob`, ciascuna con il proprio profilo, la propria porta e la clipboard simulata, e le collega tra loro.
- **Test della clipboard reale:** un'istanza con `--clipboard real` e una con `--clipboard simulated`. Si prova la clipboard di Windows contro un peer sullo stesso PC, senza conflitti. Mai due istanze `real` insieme.
- **Test automatici multi-istanza:** dalla fase 2, il nucleo espone un nodo CUS senza UI (`CusNode`). I test d'integrazione ne avviano diversi nello stesso processo, collegati in loopback con clipboard simulate.

## Fase 1 — Fondamenta locali

**Obiettivo:** un'istanza che conserva i propri dati in modo sicuro e legge e scrive la clipboard di Windows.

- **DB:**
  - schema iniziale con migrazioni versionate: identità del dispositivo, impostazioni, peer manuali;
  - apertura all'avvio da `ProfileAppPaths`.
- **Chiave del DB:**
  - `ISecretStore` su Windows con DPAPI, scope utente corrente, tramite P/Invoke su `CryptProtectData`: nessun pacchetto aggiuntivo;
  - chiave casuale da 32 byte generata al primo avvio.
- **Identità del dispositivo:** coppia di chiavi Ed25519 generata al primo avvio (`crypto_sign_keypair` di libsodium) e salvata nel DB.
- **Clipboard Windows:**
  - monitor con `AddClipboardFormatListener` su una finestra di soli messaggi;
  - lettura di testo, immagini (PNG) e liste di file (solo metadati);
  - rilevamento dei formati che marcano un contenuto come sensibile (`ExcludeClipboardContentFromMonitorProcessing`, `CanIncludeInClipboardHistory` = 0, `CanUploadToCloudClipboard` = 0), che imposta `IsMarkedSensitive`;
  - scrittura di testo e immagini.
- **Anti-rimbalzo:** le scritture fatte da CUS si riconoscono, con un formato privato o con il numero di sequenza della clipboard, e non vengono rinviate.
- **Nuovo progetto di test:** `tests/Cus.Platform.Windows.Tests`. Contiene i test di DPAPI e della clipboard reale, eseguiti in serie perché toccano la clipboard di sistema.

**Come si prova su un PC:**
- test automatici su DB, chiavi e identità, in cartelle temporanee;
- test della clipboard reale con copie fatte via API;
- avvio di `cus --clipboard real`, poi copia da un'altra applicazione: la finestra mostra il contenuto, o "testo sensibile" se viene da un password manager.

**Criteri di uscita:**
- al riavvio il DB si riapre con la chiave protetta da DPAPI, e un profilo non riesce ad aprire il DB di un altro;
- i contenuti marcati come sensibili sono riconosciuti, con un test per ciascun formato;
- nessun rimbalzo: una scrittura di CUS non genera un nuovo evento "da inviare".

## Fase 2 — Rete e sessioni

**Obiettivo:** due o più istanze condividono testo e immagini in una sessione con host, con consenso e sola lettura.

- **Nodo senza UI:** `CusNode` in `Cus.Core` riunisce rete, sessioni e clipboard dietro le interfacce, così la UI e i test lo usano allo stesso modo.
- **Trasporto:**
  - TCP con TLS (`SslStream`) e certificato autofirmato legato all'identità del dispositivo. In questa fase i fingerprint si verificano a vista; con la fase 3 si verificheranno tramite l'abbinamento;
  - protocollo a messaggi con lunghezza e versione.
- **Porte:** si prova la prima libera del range 23700–23709. Il fallback è obbligatorio, perché su questo PC il range dinamico di Windows parte da 1024.
- **Scoperta:**
  - mDNS/DNS-SD, con nome di servizio che include il profilo, così più istanze sullo stesso PC sono distinguibili;
  - peer manuali con IP o nome host.
- **Sessioni:**
  - l'host è chi invia la richiesta e ne sceglie la modalità; la notifica offre Consenti, Consenti in sola lettura (solo per un invito bidirezionale), Rifiuta;
  - le copie passano dall'host, che le distribuisce agli altri; vince l'ultima arrivata, ordinata dal numero di sequenza assegnato dall'host;
  - un membro in sola lettura riceve tutte le copie della sessione ma non invia le proprie (cambiato il 2026-11-24, vedi lo stato);
  - se l'host si disconnette, la sessione termina per tutti; un membro può uscire da solo;
  - i contenuti sensibili non lasciano mai il PC.
- **Contenuti:** testo e immagini. I file arrivano in fase 4.
- **UI minima:** lista dei peer, invio della richiesta, finestra di consenso, partecipanti nella finestra principale. L'indicatore è ancora quello simulato.
- **Nuovo progetto di test:** `tests/Cus.IntegrationTests`, con più `CusNode` nello stesso processo, collegati in loopback e con clipboard simulate.

**Come si prova su un PC:**
- test d'integrazione: sessione a due e di gruppo, rifiuto, sola lettura, copie quasi simultanee (vince l'ultima che arriva all'host), uscita di un membro, caduta dell'host, contenuto sensibile non trasmesso, nessun loop tra istanze;
- prova manuale con `run-two-instances.ps1`, più una terza istanza per il gruppo.

**Criteri di uscita:**
- tutti gli scenari sopra coperti da test d'integrazione verdi;
- una sessione di 3 istanze sullo stesso PC funziona a mano;
- il traffico in loopback è cifrato: una cattura non mostra il testo copiato.

## Fase 3 — Identità e abbinamento

> [!note]
> Superata il 2026-11-25: abbinamento con codice, CPace e lista di revoche sono stati tolti (vedi "Stato al 2026-11-25").

**Obiettivo:** i PC dello stesso utente si abbinano con un codice e si possono revocare in modo sicuro.

- **CPace-ristretto255-SHA512:**
  - implementazione in C# sopra libsodium, seguendo `draft-irtf-cfrg-cpace-21`;
  - deve passare **tutti i test vector dell'appendice B.3** prima di essere usata.
- **Codice di abbinamento:** 6 cifre, valido 2 minuti, al massimo 3 tentativi; poi serve un codice nuovo.
- **Esito dell'abbinamento:** i due dispositivi si scambiano le chiavi Ed25519 sul canale autenticato da CPace. Da qui il TLS verifica i fingerprint memorizzati invece di chiedere conferma a vista.
- **Lista firmata di abbinamenti e revoche:**
  - voci firmate Ed25519 che si aggiungono soltanto;
  - unione delle liste a ogni connessione tra PC dello stesso utente, prima di ogni altra operazione;
  - revoca reciproca: restano revocati entrambi; auto-revoca ammessa;
  - una revoca chiude subito le connessioni con il dispositivo revocato;
  - la UI elenca i dispositivi abbinati da quello revocato.
- **UI:** schermata dei dispositivi abbinati, con abbinamento, codice e revoca da qualsiasi PC dell'utente.

**Come si prova su un PC:**
- unit test con i test vector della bozza;
- test d'integrazione con tre profili dello stesso utente (`andrea-portatile`, `andrea-fisso`, `andrea-ufficio`):
  - abbinamento riuscito;
  - codice sbagliato 3 volte, poi codice scaduto;
  - revoca propagata a un profilo che era "spento", cioè non avviato;
  - revoca reciproca;
  - chiusura immediata della sessione con il dispositivo revocato.

**Criteri di uscita:**
- test vector B.3 verdi;
- un dispositivo revocato non riesce più a connettersi, in nessun ordine di avvio dei profili;
- l'unione delle liste dà lo stesso risultato in qualunque ordine di sincronizzazione.

## Fase 4 — File lazy, cronologia e indicatore reale

**Obiettivo:** l'esperienza completa descritta nello scenario di IDEA.md.

- **File lazy su Windows:**
  - alla copia si annunciano solo i metadati;
  - nella clipboard di chi riceve si usano il *delayed rendering* e i virtual files (`CFSTR_FILEDESCRIPTOR` / `CFSTR_FILECONTENTS`), così Esplora risorse incolla scaricando dal PC d'origine solo in quel momento;
  - se il PC d'origine è spento o ha già copiato altro, compare un errore chiaro.
- **Cronologia:**
  - ultime 20 voci, solo testo e immagini, nel DB cifrato dell'host, dove restano anche a fine sessione;
  - i membri la consultano durante la sessione.
- **Indicatore reale:**
  - una finestra topmost, trasparente ai clic, per ogni monitor, posizionata in alto a destra;
  - mostra partecipanti e direzione di ciascuno; il clic fa uscire dalla sessione;
  - gestisce l'aggiunta e la rimozione dei monitor e i DPI diversi.
- **UI completa:** cronologia, impostazioni, peer manuali.

**Come si prova su un PC:**
- file lazy: istanza `real` contro istanza `simulated` sullo stesso PC, con file grandi; lo spegnimento dell'istanza d'origine prima di incollare deve dare l'errore chiaro;
- cronologia: test d'integrazione su 25 copie (restano le ultime 20), sulla persistenza dopo la fine della sessione e sulla consultazione da parte di un membro;
- indicatore: test manuale su più monitor, se disponibili. In alternativa si collega e scollega un monitor virtuale, o si cambia il ridimensionamento DPI.

**Criteri di uscita:**
- un file da almeno 1 GB copiato su un'istanza e incollato in Esplora risorse dall'altra, senza trasferimento prima dell'incolla;
- l'indicatore non ruba il focus e non blocca i clic sottostanti;
- tutti i test, unitari e d'integrazione, verdi.
