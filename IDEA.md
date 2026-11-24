# CUS — Clipboard Ultra Shared

> Idea annotata il 2026-11-24. Nessun codice ancora: questo file descrive il concetto, non uno stato di sviluppo.

## L'idea in una riga

Un servizio per PC nella stessa rete locale che permette di **condividere la clipboard** con altre persone (previo consenso) o con **sé stessi su un altro PC**, in tempo reale, in modo bidirezionale o in sola lettura.

## Scenario d'uso

> **Andrea:** "Luca, mi passi il file?"
> **Luca:** "Certo, l'ho copiato ora."
> **Andrea:** "Ti invio la richiesta di accesso alla tua clipboard."
>
> *Sul PC di Luca compare: "Andrea richiede l'accesso alla tua clipboard. Consentire?"*
>
> **Luca:** "Ho accettato."
>
> *Su tutti i monitor di entrambi compare un'icona sempre in primo piano, in alto a destra, che indica che la clipboard è condivisa. Da quel momento, se Luca copia un file Andrea lo riceve, e se Andrea copia un file Luca lo riceve.*

## Due modalità

| Modalità | Chi | Consenso |
|---|---|---|
| **Stesso utente, PC diversi** | Andrea sul portatile e Andrea sul fisso | Abbinamento una tantum con codice; poi i dispositivi si riconoscono finché non vengono revocati |
| **Utenti diversi** | Andrea, Luca, eventualmente altri | Richiesta esplicita, accettata da ciascun membro a ogni sessione |

## Sessioni e ruoli

- **Host:** chi avvia la sessione e invia le richieste, anche nelle sessioni a due. Nello scenario sopra è Andrea.
- **Membri:** chi riceve la richiesta e la accetta. Ogni membro dà il consenso **all'host**, non agli altri membri.
- **Distribuzione:** ogni copia, di un membro o dell'host, va all'host, che la distribuisce a tutti gli altri.
- **Conflitti:** **vince sempre l'ultima copia arrivata all'host.** L'ordine lo decide solo l'host, non gli orologi dei PC.
- **Sola lettura:** la decide **chi concede** l'accesso ("puoi solo leggere"). Quel membro invia le proprie copie alla sessione, ma non riceve quelle degli altri.
- **Indicatore:** mostra chi è nella sessione e la direzione attiva per ciascuno (bidirezionale o sola lettura).
- **Cronologia:** sta solo sull'host. I membri la consultano durante la sessione, chiedendola all'host. A fine sessione le voci restano nel DB dell'host.
- **Uscita dell'host:** se l'host si disconnette, la sessione termina per tutti. Non c'è passaggio di ruolo.

## Flusso

1. Ogni PC esegue il servizio CUS, che si annuncia sulla rete locale (scoperta automatica dei peer).
2. L'host sceglie uno o più peer dalla lista, oppure ne inserisce IP o nome host a mano, e invia la richiesta.
3. Ogni destinatario vede una notifica con tre scelte: **Consenti**, **Consenti in sola lettura**, **Rifiuta**.
4. Per chi accetta si apre la sessione:
   - su **tutti i monitor** compare l'indicatore sempre in primo piano in alto a destra, con i partecipanti;
   - le copie passano dall'host e da lì vanno ai membri, rispettando la sola lettura.
5. Un membro esce cliccando sull'indicatore. La sessione intera termina quando la chiude l'host o quando l'host si disconnette.

## Requisiti chiave

- **Trasparenza:** l'indicatore sempre visibile è non negoziabile: chi condivide deve sempre sapere che la clipboard è esposta. Deve stare sopra le altre finestre, ma senza rubare il focus né intercettare i clic sul contenuto sottostante.
- **Revoca immediata:** un clic sull'indicatore toglie subito il proprio PC dalla condivisione.
- **Contenuti supportati:** testo, rich text/HTML, immagini, file e cartelle (i file si trasferiscono davvero, non solo il percorso).
- **Solo rete locale:** nessun server esterno; il traffico resta nella LAN.
- **Cifratura:** il canale tra i peer è cifrato e i peer sono autenticati, perché la clipboard contiene spesso password e dati sensibili.

## Decisioni prese (2026-11-24)

| Tema | Decisione |
|---|---|
| **Direzione** | Bidirezionale oppure **sola lettura**, decisa da chi concede l'accesso. |
| **Gruppi** | Sessioni di gruppo con un **host**: tutti danno il consenso all'host, l'host distribuisce le copie e vince sempre l'ultima che gli arriva. |
| **Indicatore** | Mostra i partecipanti alla sessione. |
| **File grandi** | **Lazy:** alla copia si annunciano solo i metadati (nome, dimensione, tipo). Il contenuto si trasferisce quando l'altro incolla. |
| **Peer d'origine non disponibile** | Se si incolla un file e il PC d'origine è spento o ha già copiato altro, compare un **errore chiaro**. Nessun tentativo silenzioso. |
| **Filtri di sicurezza** | Si **esclude in automatico** ciò che i password manager marcano come sensibile: quei contenuti non lasciano mai il PC. |
| **Identità** | **Abbinamento con codice**, per utente. Il codice alimenta uno **scambio di chiavi autenticato da password: CPace-ristretto255-SHA512**, implementato in C# sopra **libsodium** (vedi sotto). Niente fiducia implicita basata su nome utente o dominio. |
| **Codice di abbinamento** | **6 cifre**, valido **2 minuti**, al massimo **3 tentativi**; poi serve un codice nuovo. |
| **Revoca** | Un dispositivo abbinato si revoca **da qualsiasi PC dello stesso utente**. La revoca finisce in una **lista firmata, sincronizzata tra i dispositivi dell'utente**: un PC spento la riceve alla prossima connessione con un altro PC dell'utente. Una sessione attiva con il dispositivo revocato **si chiude subito**. |
| **Host** | Chi invia la richiesta, anche nelle sessioni a due. Se si disconnette, la sessione **termina per tutti**. |
| **Cronologia** | Le **ultime 20 voci**, salvate su disco **cifrate nel DB dell'host**, dove **restano anche a fine sessione**. I membri le **consultano durante la sessione**. Si salvano solo testo e immagini; i file restano riferimenti lazy al PC d'origine. |
| **Piattaforme** | **Per ora solo Windows.** Il codice è predisposto per un porting a **Linux** senza riscritture (vedi "Predisposizione per Linux"). macOS escluso. |
| **Reti aziendali** | Se la scoperta automatica fallisce, si inserisce a mano **l'IP o il nome host**. |
| **Porte** | **TCP/UDP 23700–23709** (vedi sotto). |
| **Linguaggio** | **C#** su **.NET 10**. |
| **Persistenza** | **SQLite cifrato**, un file per utente nella cartella dati dell'utente (`AppData`). |
| **UI** | **Avalonia**. |
| **Cifratura DB** | **SQLite3 Multiple Ciphers** (SQLite3MC), verificato il 2026-11-24 (vedi sotto). |

## Stack

- **Linguaggio:** C# su .NET 10. .NET 10 è anche un requisito di SQLite3MC (vedi sotto). Le API della clipboard di sistema si chiamano tramite P/Invoke: per ora Win32; X11/Wayland quando arriverà Linux.
- **UI:** Avalonia, per finestre, notifiche di consenso e cronologia. È multipiattaforma, quindi la UI non andrà riscritta per Linux. L'indicatore sempre in primo piano richiede comunque codice nativo: per ora una finestra topmost trasparente ai clic su Windows.

### Predisposizione per Linux

Si sviluppa e si rilascia solo per Windows, ma con queste regole, così il porting a Linux sarà un'aggiunta e non una riscrittura:

- **Nucleo indipendente dalla piattaforma:** protocollo, sessioni, host, CPace, cronologia, DB e UI non usano API di Windows. Nessun `#if WINDOWS` e nessun P/Invoke fuori dai progetti di piattaforma.
- **Interfacce per tutto ciò che è nativo**, con per ora solo l'implementazione Windows:
  - monitoraggio della clipboard e scrittura, incluso il lazy (`IClipboardMonitor`, `IClipboardWriter`);
  - riconoscimento dei contenuti marcati come sensibili dai password manager. Lo fa il monitor della clipboard, che imposta `ClipboardContent.IsMarkedSensitive`; il nucleo garantisce che quei contenuti non lascino il PC;
  - indicatore su tutti i monitor (`IOverlayIndicator`);
  - protezione della chiave del DB (`ISecretStore`: DPAPI su Windows, Secret Service su Linux);
  - percorsi dei dati (`IAppPaths`).
- **Struttura dei progetti:** `Cus.Core`, `Cus.UI` (Avalonia), `Cus.Platform.Windows`, `Cus.Platform.Simulated` (test e più istanze sullo stesso PC) e l'eseguibile `Cus.App.Windows`. In futuro si aggiungeranno `Cus.Platform.Linux` e `Cus.App.Linux` accanto, senza toccare gli altri. Dettagli in [PLAN.md](PLAN.md).
- **Dipendenze solo multipiattaforma:** si accettano solo pacchetti che funzionano anche su Linux. SQLite3MC e libsodium lo fanno già.
- **Test:** i test del nucleo non dipendono da Windows. In CI possono girare anche su Linux da subito, come controllo che il nucleo resti portabile.
- **Percorsi:** si usa `Environment.SpecialFolder.LocalApplicationData` e non `%LOCALAPPDATA%` scritto a mano, così su Linux diventa da solo `~/.local/share`.
- **Database:** SQLite cifrato con SQLite3MC, tramite `Microsoft.Data.Sqlite.Core` e `SQLite3MC.PCLRaw.bundle`. È accessibile solo all'utente che esegue CUS.
  - **Windows:** `%LOCALAPPDATA%\CUS\cus.db`. Si usa `LocalAppData` e non `Roaming`, perché le chiavi dei dispositivi abbinati sono legate a quel PC.
  - **Linux (futuro):** `~/.local/share/CUS/cus.db`. In .NET è `Environment.SpecialFolder.LocalApplicationData`, che rispetta `XDG_DATA_HOME`. Il file va creato con permessi `0600`.
- **Contenuto del DB:**
  - identità del dispositivo (coppia di chiavi Ed25519);
  - dispositivi e utenti abbinati (fingerprint, data, stato di revoca);
  - lista firmata di abbinamenti e revoche, condivisa con gli altri PC dello stesso utente;
  - IP e nomi host inseriti a mano;
  - le ultime 20 voci della cronologia, solo sul PC che fa da host;
  - impostazioni.
- **Chiave del DB:** non va scritta in chiaro accanto al file.
  - **Windows:** una chiave casuale protetta con **DPAPI**, nello scope dell'utente corrente (`ProtectedData`).
  - **Linux (futuro):** una chiave casuale conservata nel **Secret Service** (libsecret, cioè GNOME Keyring o KWallet). Come ripiego, un file `0600`, che però protegge meno.

### Verifica di SQLite3MC (2026-11-24)

| Voce | Esito |
|---|---|
| Licenza | **MIT**, sia la libreria (`utelle/SQLite3MultipleCiphers`) sia il pacchetto NuGet (`SQLite3MC.PCLRaw.bundle`). |
| Manutenzione | Attiva: ultima release **v2.7.1** del 2026-11-15, ultimo push sul repository il 2026-11-23, repository non archiviato. |
| Pacchetto da usare | **`SQLite3MC.PCLRaw.bundle`** 2.7.1, mantenuto dall'autore di SQLite3MC. |
| Pacchetto da evitare | `SQLitePCLRaw.bundle_e_sqlite3mc`: fermo alla 2.1.11 (dicembre 2024). |
| Piattaforme native | Include `win-x64`, `win-arm64`, `win-x86`, `linux-x64`, `linux-arm64`, `linux-musl-x64` e altre. Copre Windows e Linux. |

Vincoli d'uso, dal README del pacchetto:

- Si usa `Microsoft.Data.Sqlite.Core`, non `Microsoft.Data.Sqlite`. Il pacchetto principale porta con sé un altro bundle SQLitePCLRaw, e due bundle nello stesso progetto vanno in conflitto.
- La password si passa nella stringa di connessione (`Password=...`). Questo funziona solo da **.NET 10** in poi: nelle versioni precedenti dà errore, a causa di un cambiamento di SQLite dalla 3.48.0.
- Il cipher di default va bene. Il formato compatibile SQLCipher (`cipher=sqlcipher&legacy=4`) serve solo se si devono aprire DB SQLCipher esistenti, e non è il nostro caso.

Fonti: [SQLite3MC.PCLRaw.bundle su NuGet](https://www.nuget.org/packages/SQLite3MC.PCLRaw.bundle/), [README del pacchetto](https://github.com/utelle/SQLite3MultipleCiphers-NuGet), [repository SQLite3MultipleCiphers](https://github.com/utelle/SQLite3MultipleCiphers).

### Valutazione di CPace (2026-11-24)

CPace è uno scambio di chiavi autenticato da password di tipo bilanciato: i due PC conoscono lo stesso segreto, cioè il codice a 6 cifre. È il modello giusto per abbinare due dispositivi.

- **Standard:** bozza IRTF CFRG `draft-irtf-cfrg-cpace`. L'ultima revisione trovata è la -21 (23 aprile 2026), in revisione IESG, quindi non è ancora un RFC.
- **Suite scelta: CPace-ristretto255-SHA512.** libsodium ha già le funzioni che servono (`crypto_core_ristretto255_from_hash`, `crypto_scalarmult_ristretto255`, `crypto_core_ristretto255_scalar_random`), quindi non serve scrivere matematica sulle curve a mano. La -21 contiene i test vector per questa suite (appendice B.3), verificato il 2026-11-24.
- **Librerie .NET:** nessuna trovata. Le implementazioni esistenti sono in C (`jedisct1/cpace`, sopra libsodium), Rust, Go e Python, e quelle in Rust e Go sono dichiarate sperimentali.
- **Implementazione:** in C#, con le primitive di libsodium chiamate tramite P/Invoke.
  - **Riferimento normativo:** la bozza -21 e i suoi test vector.
  - `jedisct1/cpace` (BSD-2-Clause) è utile solo come esempio di struttura: è fermo al 2020, quindi segue una revisione vecchia della bozza, e la costruzione dei messaggi può essere diversa.
- **Verifica:** l'implementazione deve passare tutti i test vector dell'appendice B.3 prima di essere usata.
- **Sicurezza del codice:** con 6 cifre e 3 tentativi, un attaccante attivo ha al massimo 3 probabilità su un milione di indovinare. CPace impedisce di provare i codici offline, quindi il limite dei tentativi basta.
- **Dipendenza approvata (2026-11-24):** libsodium, pacchetto NuGet `libsodium` 1.0.22 (release del 2026-04-09), licenza ISC, con le librerie native per Windows e Linux. Il pacchetto richiede l'accettazione esplicita della licenza al momento dell'installazione.

Fonti: [draft-irtf-cfrg-cpace su Datatracker](https://datatracker.ietf.org/doc/draft-irtf-cfrg-cpace/), [bozza -21](https://mirror.apps.cam.ac.uk/pub/doc/internet-drafts/draft-irtf-cfrg-cpace-21.txt), [jedisct1/cpace](https://github-redirect.dependabot.com/jedisct1/cpace), [crate Rust cpace](https://docs.rs/cpace), [filippo.io/cpace](https://pkg.go.dev/filippo.io/cpace?tab=doc), [cpace su PyPI](https://pypi.org/project/cpace/).

### Lista di revoche firmata (decisa il 2026-11-24)

**Obiettivo:** un dispositivo compromesso non deve mai poter riottenere l'accesso, né bloccare per sempre gli altri dispositivi dell'utente.

**Chiavi:**

- Al primo avvio ogni dispositivo genera una **coppia di chiavi Ed25519** (`crypto_sign` di libsodium), che è la sua identità. La chiave privata sta nel DB cifrato e non lascia mai il PC.
- Non esiste una chiave "master" dell'utente. Una chiave master andrebbe copiata su tutti i PC, e un solo PC compromesso esporrebbe tutto.

**Voce di revoca:** contiene l'ID del dispositivo revocato, l'ID del dispositivo che revoca e la data. È firmata con la chiave del dispositivo che revoca.

**Regole:**

1. **Chi può revocare:** una revoca è valida solo se chi la firma era abbinato all'utente e non era già revocato.
2. **Revoca definitiva:** un dispositivo revocato non torna mai valido con la stessa chiave. Per riabbinarlo si genera una nuova identità e si ripete l'abbinamento con codice.
3. **Lista solo in aggiunta:** le voci si aggiungono e non si tolgono mai. Due PC che si incontrano fanno l'unione delle loro liste: niente conflitti di ordine, e l'unione dà sempre lo stesso risultato.
4. **Revoca reciproca:** se A revoca B e B revoca A, per esempio perché B è compromesso e cerca di difendersi, **restano revocati entrambi**. L'utente riabbina il dispositivo legittimo con un codice nuovo. È un fallimento sicuro: il dispositivo compromesso può creare disagio, ma non mantiene l'accesso.
5. **Auto-revoca:** un dispositivo può revocare sé stesso, per esempio quando si disinstalla CUS o si dismette il PC.
6. **Effetto immediato:** quando un PC riceve una revoca, chiude subito ogni connessione con il dispositivo revocato e rifiuta quelle nuove.
7. **Propagazione:** a ogni connessione tra due PC dello stesso utente si scambia la lista, prima di qualsiasi altra operazione.

**Abbinamenti fatti da un dispositivo revocato:** anche ogni abbinamento è una voce firmata, con chi l'ha fatto e quando. Quando si revoca un dispositivo, la UI elenca i dispositivi che lui ha abbinato, così l'utente può decidere se revocare anche quelli. Non si revocano in automatico, per non trascinare fuori anche dispositivi legittimi.

> [!note]
> **Cosa questo schema non impedisce:** un dispositivo compromesso, finché non è revocato, può revocare tutti gli altri. È un disservizio, recuperabile riabbinando i PC, e non una fuga di dati. Impedirlo richiederebbe un'autorità centrale o un quorum di dispositivi, una complessità sproporzionata per una LAN.

### Porte: 23700–23709

- **Perché questo range:** nel registro IANA è libero (fa parte di un blocco non assegnato da 23547 a 23999). Sta anche sotto i range effimeri di default: Linux usa 32768–60999, Windows 49152–65535.
- **Uso previsto:** 23700 per la connessione TCP (sessioni e trasferimenti) e 23701 per la scoperta via UDP, se non si usa mDNS. 23702–23709 sono riserve, se le prime sono occupate.
- **Fallback:** se la porta è occupata, CUS prova la successiva del range e annuncia via mDNS quella effettiva. Nell'inserimento manuale si accetta anche `host:porta`.

> [!warning]
> Su alcuni PC il range dinamico di Windows è stato allargato. Sul PC di sviluppo, al 2026-11-24, va da 1024 a 65535 (`netsh int ipv4 show dynamicport tcp`). In quel caso qualunque porta può essere occupata da una connessione in uscita: il fallback sul range non è facoltativo. Va anche documentata la regola firewall da aprire.

## Questioni aperte

Nessuna al 2026-11-24: tutte le domande emerse finora hanno una decisione.

## Note tecniche iniziali (da validare)

- **Scoperta peer:** mDNS/DNS-SD. Se fallisce si usa l'IP o il nome host inserito a mano.
- **Trasporto:** TLS su TCP con certificati autofirmati e fingerprint verificato all'abbinamento, oppure un protocollo tipo Noise.
- **Evitare i loop:** non deve succedere che un contenuto ricevuto venga scritto in clipboard, rilevato come nuova copia e rimandato indietro. Le voci scritte da CUS vanno marcate o riconosciute.
- **Ordine delle copie:** l'host assegna un numero di sequenza a ogni copia che riceve; i membri applicano solo copie con numero maggiore dell'ultima vista.

### Windows

- **Monitoraggio:** `AddClipboardFormatListener` / `WM_CLIPBOARDUPDATE`.
- **Lazy:** si usa il *delayed rendering*, cioè `SetClipboardData` con handle `NULL` e la risposta a `WM_RENDERFORMAT`. Per i file servono i "virtual files" (`CFSTR_FILEDESCRIPTOR` / `CFSTR_FILECONTENTS`): così si incollano in Esplora risorse senza scaricarli prima.
- **Filtro password:** si rispettano i formati `ExcludeClipboardContentFromMonitorProcessing`, `CanIncludeInClipboardHistory` = 0 e `CanUploadToCloudClipboard` = 0.
- **Indicatore:** una finestra topmost, trasparente ai clic, per ciascun monitor. Va gestito il collegamento e scollegamento dei monitor, con DPI diversi.

### Linux (futuro, non nel primo rilascio)

Note da tenere per il porting. Spiegano perché le interfacce di piattaforma sono fatte così.

- **X11:**
  - Monitoraggio con l'estensione XFixes (`XFixesSelectSelectionInput`).
  - Il lazy viene naturale, perché il proprietario della selezione fornisce i dati solo su richiesta.
  - Indicatore come finestra override-redirect o `_NET_WM_STATE_ABOVE`.
- **Wayland:** è la parte più difficile.
  - Monitorare la clipboard in background richiede protocolli come `wlr-data-control` / `ext-data-control`, non disponibili su tutti i compositor (GNOME non li espone).
  - L'indicatore sempre in primo piano richiede `wlr-layer-shell`, anch'esso non universale.
  - Va deciso quali ambienti supportare, oppure ripiegare su un'icona nella tray.
- **Filtro password:** si rispetta il MIME type `x-kde-passwordManagerHint` = `secret`, usato da KeePassXC e altri.
- **File:** si incollano come `text/uri-list` e servono file reali, quindi il lazy richiede un trucco: scaricare al momento dell'incolla, oppure usare un filesystem virtuale (FUSE) che presenta i file remoti.
