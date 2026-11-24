# CUS — Piano di sviluppo

> Creato il 2026-11-24. Copre solo lo sviluppo, fino a un'applicazione Windows completa e provata. Restano fuori installer, firma del codice, distribuzione, aggiornamenti automatici e porting a Linux. Requisiti e decisioni sono in [IDEA.md](IDEA.md).

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
  - l'host è chi invia la richiesta, e la notifica offre Consenti, Consenti in sola lettura, Rifiuta;
  - le copie passano dall'host, che le distribuisce agli altri; vince l'ultima arrivata, ordinata dal numero di sequenza assegnato dall'host;
  - un membro in sola lettura invia le proprie copie ma non riceve quelle degli altri;
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
