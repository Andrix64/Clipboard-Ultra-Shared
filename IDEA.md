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

> [!note]
> **Ridisegno del 2026-11-25:** il collegamento funziona come AirDrop, l'abbinamento con codice è stato tolto, ci sono amici con patti reciproci e una fiducia per le password. Le regole valide sono nella sezione "Modello del 2026-11-25" qui sotto; le righe superate delle tabelle successive sono marcate.

## Modello del 2026-11-25

- **Visibilità (come AirDrop):** un PC si annuncia in rete solo se l'utente sceglie **Offriti come host**. Chi lo vede chiede di entrare; l'host decide per ognuno se farlo entrare in **bidirezionale** o in **sola lettura**. Quando sono entrati tutti, l'host si **nasconde**: la sessione resta, ma nessun altro può più chiedere. Gli altri vedono nell'elenco **quante persone** ci sono nella sessione di ciascun host visibile.
- **Permessi in sessione:** l'host cambia la modalità di ogni connesso con un pulsante che alterna bidirezionale e sola lettura. Un membro con accesso bidirezionale può passare da sé a sola lettura e tornare indietro (pulsante nell'indicatore e nella scheda della sessione).
- **Patti (amici):** dopo averlo fatto entrare, l'host può proporre a un connesso un patto, che il connesso deve accettare. Due livelli: **entra senza conferma** (quando l'altro si offre come host) e **collegamento in qualsiasi momento** (anche se l'altro non si offre; rifiutato con "X è già in una condivisione" se l'altro è membro di un'altra sessione). Il patto vale nei due sensi; se uno dei due lo scioglie cade per entrambi: a ogni connessione i due PC si scambiano il proprio livello e tengono il più basso. Un amico "in qualsiasi momento" si raggiunge all'ultimo indirizzo noto (scelta del 2026-11-25: da nascosto un PC non annuncia nulla, nemmeno un segnale anonimo).
- **Password (contenuti sensibili):** ognuno decide per le proprie copie. Una copia marcata sensibile va solo agli amici marcati **affidabili** (decisione di un solo lato, dalla scheda AMICI o dall'elenco dei connessi). L'host può dare un **permesso temporaneo**, valido solo per la sessione in corso: le password che copia lui arrivano a tutti. Il permesso copre solo le copie dell'host (scelta del 2026-11-25); quella di un membro passa dall'host solo se il membro si fida dell'host, e l'host la inoltra solo ai partecipanti di cui il membro si fida. Chi la riceve vede "contenuto sensibile" e la sua clipboard la tratta ancora come sensibile; le password non entrano in cronologia.
- **File:** copiare altro non interrompe un trasferimento già in corso. Il proprietario dei file vede dall'indicatore le copie in corso (a chi vanno, a che punto sono) e le può annullare. Un **nuovo** incolla di file superati dà ancora l'errore chiaro.
- **Indicatore:** mostra l'ultimo contenuto condiviso (chi l'ha copiato a sinistra, il peso a destra, sotto "testo di N caratteri" o il nome del file accorciato in mezzo con l'estensione sempre visibile) e i partecipanti in piccolo, con un'icona per il ruolo (★ host, ⇄ bidirezionale, ↓ sola lettura) e questo PC evidenziato.
- **Sempre attivo:** alla chiusura della finestra CUS resta nell'area di notifica (impostazione, attiva di default); "Esci" dal menu dell'icona lo chiude davvero.
- **Interfaccia:** tre corpi. A sinistra cronologia e impostazioni, al centro la console verde, a destra la ricerca in rete (o, quando ci si offre o si è in sessione, richieste e connessi) e la scheda AMICI.
- **Fine della sessione e nuovi arrivati (revisione del 2026-11-25):** quando esce l'ultimo membro la sessione finisce anche se l'host resta visibile, e ne parte una nuova, vuota. Chi entra dopo non vede la cronologia dei membri di prima e non eredita il permesso temporaneo per le password. Chi entra a sessione già avviata vede invece la cronologia della sessione in corso.
- **Richieste insistenti:** dopo una richiesta di ingresso rifiutata, scaduta o annullata, lo stesso dispositivo e lo stesso indirizzo (loopback escluso) aspettano 30 s prima di poter chiedere di nuovo. Chi chiede di entrare mentre si offre come host torna visibile se la richiesta non va a buon fine.
- **Nascosto davvero (2026-11-25):** da nascosto il PC non si annuncia e resta in ascolto solo per gli amici con un patto. Nell'handshake (protocollo 2) le identità viaggiano cifrate e chi si collega si presenta per primo, con la firma; un PC nascosto risponde con la propria identità solo a un amico con patto, a tutti gli altri solo "rifiutato". Chi prova le porte capisce che lì c'è CUS, non quale PC né di chi. Gli annunci (anche quelli di "nascosto") sono firmati con la chiave del PC e numerati (2026-11-25): nessuno in rete può più far sparire un host dall'elenco altrui, nemmeno ripetendo un suo vecchio congedo.
- **Patto sciolto e PC nascosto:** se un PC scioglie un patto mentre l'altro è spento e poi resta nascosto, l'altro non riesce più a collegarsi (non è più un amico) e il patto cade anche dal suo lato alla prima connessione riuscita: quando il primo torna visibile, o quando è lui a contattare l'altro.
- **Nomi uguali a quelli di un amico:** chi è un amico lo dice l'ID verificato, mai il nome. Se un PC che non è un amico ha lo stesso nome di uno (a occhio: maiuscole, spazi, accenti, lettere cirilliche e greche simili alle latine, 0/O, 1/l/I), l'elenco lo segnala con ⚠ e chiede conferma prima di entrare, e l'host vede l'avviso nella richiesta di ingresso. Per chi non è un amico l'elenco mostra sempre l'impronta, che la connessione verifica.
- **File ricevuti:** incollati da Esplora risorse portano il Mark-of-the-Web (zona Internet), quindi SmartScreen e Office li trattano come scaricati. Chi riceve file che avviano programmi (eseguibili, script, collegamenti, installer, documenti con macro, immagini disco) vede un avviso. La dimensione totale dichiarata è limitata a 1 TiB.
- **Invii dell'host:** ogni membro ha una coda ordinata, svuotata fuori dal lock della sessione. Un membro che non legge più non rallenta gli altri: con la coda piena (64 messaggi o 32 MB) viene scollegato.

## Due modalità (superato il 2026-11-25)

| Modalità | Chi | Consenso |
|---|---|---|
| **Stesso utente, PC diversi** | Andrea sul portatile e Andrea sul fisso | Abbinamento una tantum con codice; poi i dispositivi si riconoscono finché non vengono revocati |
| **Utenti diversi** | Andrea, Luca, eventualmente altri | Richiesta esplicita, accettata da ciascun membro a ogni sessione |

## Sessioni e ruoli

> [!note]
> Superato in parte il 2026-11-25: non c'è più l'invito dell'host, si chiede di entrare e l'host sceglie la modalità; vale "Modello del 2026-11-25". Restano validi distribuzione, conflitti, cronologia e uscita dell'host.

- **Host:** chi avvia la sessione e invia le richieste, anche nelle sessioni a due. Nello scenario sopra è Andrea.
- **Membri:** chi riceve la richiesta e la accetta. Ogni membro dà il consenso **all'host**, non agli altri membri.
- **Distribuzione:** ogni copia, di un membro o dell'host, va all'host, che la distribuisce a tutti gli altri.
- **Conflitti:** **vince sempre l'ultima copia arrivata all'host.** L'ordine lo decide solo l'host, non gli orologi dei PC.
- **Sola lettura:** la sceglie **chi invita**, per ogni invito. Quel membro riceve tutte le copie della sessione (dell'host e degli altri membri), ma le sue restano sul suo PC. Chi riceve un invito bidirezionale può accettarlo solo in sola lettura; un invito in sola lettura non si può allargare.
- **Indicatore:** mostra chi è nella sessione e la direzione attiva per ciascuno (bidirezionale o sola lettura).
- **Cronologia:** sta solo sull'host. I membri la consultano durante la sessione, chiedendola all'host, e vedono solo le voci della sessione in corso. A fine sessione le voci restano nel DB dell'host.
- **Uscita dell'host:** se l'host si disconnette, la sessione termina per tutti. Non c'è passaggio di ruolo.

## Flusso (superato il 2026-11-25)

> [!note]
> Il flusso con l'invito dell'host e i dispositivi abbinati è stato sostituito: vedi "Modello del 2026-11-25".

1. Ogni PC esegue il servizio CUS, che si annuncia sulla rete locale (scoperta automatica dei peer).
2. L'host sceglie uno o più peer dalla lista, oppure ne inserisce IP o nome host a mano, sceglie la modalità (bidirezionale o sola lettura) e invia la richiesta.
3. Ogni destinatario vede una notifica: per un invito bidirezionale **Consenti**, **Consenti in sola lettura**, **Rifiuta**; per un invito in sola lettura **Consenti** e **Rifiuta**. Un dispositivo abbinato accetta da solo, nella modalità scelta dall'host.
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
| **Direzione** | Bidirezionale oppure **sola lettura**, scelta da chi invita; chi accetta può solo ridurla a sola lettura. In sola lettura il membro riceve ma non invia. Cambiata il 2026-11-24: prima la decideva chi concedeva l'accesso, e il membro inviava senza ricevere. |
| **Gruppi** | Sessioni di gruppo con un **host**: tutti danno il consenso all'host, l'host distribuisce le copie e vince sempre l'ultima che gli arriva. |
| **Indicatore** | Mostra i partecipanti alla sessione. |
| **File grandi** | **Lazy:** alla copia si annunciano solo i metadati (nome, dimensione, tipo). Il contenuto si trasferisce quando l'altro incolla. |
| **Peer d'origine non disponibile** | Se si incolla un file e il PC d'origine è spento o ha già copiato altro, compare un **errore chiaro**. Nessun tentativo silenzioso. |
| **Filtri di sicurezza** | *Cambiato il 2026-11-25: le password vanno agli amici affidabili o a tutti col permesso temporaneo dell'host (vedi "Modello del 2026-11-25").* Si **esclude in automatico** ciò che i password manager marcano come sensibile: quei contenuti non lasciano mai il PC. |
| **Identità** | *Superato il 2026-11-25: niente più abbinamento con codice; l'identità resta la chiave Ed25519 verificata dal canale, e gli amici si legano al suo ID.* **Abbinamento con codice**, per utente. Il codice alimenta uno **scambio di chiavi autenticato da password: CPace-ristretto255-SHA512**, implementato in C# sopra **libsodium** (vedi sotto). Niente fiducia implicita basata su nome utente o dominio. |
| **Codice di abbinamento** | **6 cifre**, valido **2 minuti**, al massimo **3 tentativi**; poi serve un codice nuovo. |
| **Revoca** | *Superato il 2026-11-25: al posto della lista di revoche ci sono patti che cadono per entrambi.* Un dispositivo abbinato si revoca **da qualsiasi PC dello stesso utente**. La revoca finisce in una **lista firmata, sincronizzata tra i dispositivi dell'utente**: un PC spento la riceve alla prossima connessione con un altro PC dell'utente. Una sessione attiva con il dispositivo revocato **si chiude subito**. |
| **Host** | Chi invia la richiesta, anche nelle sessioni a due. Se si disconnette, la sessione **termina per tutti**. |
| **Cronologia** | Le **ultime 20 voci**, salvate su disco **cifrate nel DB dell'host**, dove **restano anche a fine sessione**. I membri le **consultano durante la sessione**. Si salvano solo testo e immagini; i file restano riferimenti lazy al PC d'origine. |
| **Piattaforme** | **Per ora solo Windows.** Il codice è predisposto per un porting a **Linux** senza riscritture (vedi "Predisposizione per Linux"). macOS escluso. |
| **Reti aziendali** | Se la scoperta automatica fallisce, si inserisce a mano **l'IP o il nome host**. |
| **Porte** | **TCP/UDP 23700–23709** (vedi sotto). |
| **Linguaggio** | **C#** su **.NET 10**. |
| **Persistenza** | **SQLite cifrato**, un file per utente nella cartella dati dell'utente (`AppData`). |
| **UI** | **Avalonia**. |
| **Cifratura DB** | **SQLite3 Multiple Ciphers** (SQLite3MC), verificato il 2026-11-24 (vedi sotto). |
| **Interfaccia** | Skin "lettore retrò": telaio in metallo scuro, display LCD verde lime, pulsanti tondi lucidi (richiesta del 2026-11-24). |

## Stack

- **Linguaggio:** C# su .NET 10. .NET 10 è anche un requisito di SQLite3MC (vedi sotto). Le API della clipboard di sistema si chiamano tramite P/Invoke: per ora Win32; X11/Wayland quando arriverà Linux.
- **UI:** Avalonia, per finestre, notifiche di consenso e cronologia. È multipiattaforma, quindi la UI non andrà riscritta per Linux. L'indicatore sempre in primo piano richiede comunque codice nativo: per ora una finestra topmost trasparente ai clic su Windows.

### Predisposizione per Linux

Si sviluppa e si rilascia solo per Windows, ma con queste regole, così il porting a Linux sarà un'aggiunta e non una riscrittura:

- **Nucleo indipendente dalla piattaforma:** protocollo, sessioni, host, amici, cronologia, DB e UI non usano API di Windows. Nessun `#if WINDOWS` e nessun P/Invoke fuori dai progetti di piattaforma.
- **Interfacce per tutto ciò che è nativo**, con per ora solo l'implementazione Windows:
  - monitoraggio della clipboard e scrittura, incluso il lazy (`IClipboardMonitor`, `IClipboardWriter`);
  - riconoscimento dei contenuti marcati come sensibili dai password manager. Lo fa il monitor della clipboard, che imposta `ClipboardContent.IsMarkedSensitive`; il nucleo li manda solo a chi può vederli (vedi "Password" nel modello del 2026-11-25);
  - indicatore su tutti i monitor (`IOverlayIndicator`);
  - protezione della chiave del DB (`ISecretStore`: DPAPI su Windows, Secret Service su Linux);
  - percorsi dei dati (`IAppPaths`).
- **Struttura dei progetti:** `Cus.Core`, `Cus.UI` (Avalonia), `Cus.Platform.Windows`, `Cus.Platform.Simulated` (test e più istanze sullo stesso PC) e l'eseguibile `Cus.App.Windows`. In futuro si aggiungeranno `Cus.Platform.Linux` e `Cus.App.Linux` accanto, senza toccare gli altri. Dettagli in [PLAN.md](PLAN.md).
- **Dipendenze solo multipiattaforma:** si accettano solo pacchetti che funzionano anche su Linux. SQLite3MC e libsodium lo fanno già.
- **Test:** i test del nucleo non dipendono da Windows. In CI possono girare anche su Linux da subito, come controllo che il nucleo resti portabile.
- **Percorsi:** si usa `Environment.SpecialFolder.LocalApplicationData` e non `%LOCALAPPDATA%` scritto a mano, così su Linux diventa da solo `~/.local/share`.
- **Database:** SQLite cifrato con SQLite3MC, tramite `Microsoft.Data.Sqlite.Core` e `SQLite3MC.PCLRaw.bundle`. È accessibile solo all'utente che esegue CUS.
  - **Windows:** `%LOCALAPPDATA%\CUS\cus.db`. Si usa `LocalAppData` e non `Roaming`, perché identità e amici sono legati a quel PC.
  - **Linux (futuro):** `~/.local/share/CUS/cus.db`. In .NET è `Environment.SpecialFolder.LocalApplicationData`, che rispetta `XDG_DATA_HOME`. Il file va creato con permessi `0600`.
- **Contenuto del DB:**
  - identità del dispositivo (coppia di chiavi Ed25519);
  - amici: patto, fiducia per le password e ultimo indirizzo noto (dal 2026-11-25, al posto di dispositivi abbinati e lista di revoche);
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

### Valutazione di CPace (2026-11-24, rimosso il 2026-11-25)

> [!note]
> CPace, l'abbinamento con codice e la lista di revoche firmata sono stati tolti dal codice il 2026-11-25 insieme ai loro test. Questa sezione e la successiva restano come storia della decisione.

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

### Lista di revoche firmata (decisa il 2026-11-24, rimossa il 2026-11-25)

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
- **Uso previsto:** TCP dalla prima libera tra 23700 e 23709 per sessioni e trasferimenti; UDP 23701 (multicast 239.255.23.70) per la scoperta.
- **Fallback:** se la porta è occupata, CUS prova la successiva del range e la annuncia nella scoperta. Nell'inserimento manuale si accetta anche `host:porta`.

> [!warning]
> Su alcuni PC il range dinamico di Windows è stato allargato. Sul PC di sviluppo, al 2026-11-24, va da 1024 a 65535 (`netsh int ipv4 show dynamicport tcp`). In quel caso qualunque porta può essere occupata da una connessione in uscita: il fallback sul range non è facoltativo. Va anche documentata la regola firewall da aprire.

## Questioni aperte

Nessuna al 2026-11-25. Le quattro emerse dalla revisione di sicurezza (saluto da nascosto, nomi falsi, file ricevuti, invii sotto lock) sono state risolte lo stesso giorno: vedi "Modello del 2026-11-25".

## Note tecniche iniziali (da validare)

- **Scoperta peer (implementata, 2026-11-24):** annunci UDP multicast. Dal 2026-11-25 sono firmati con la chiave Ed25519 del PC (l'ID deve corrispondere alla chiave) e portano un contatore crescente (millisecondi Unix, mai più basso del precedente): chi ascolta scarta firme non valide e contatori non più alti dell'ultimo visto da quel PC, così un annuncio registrato e ripetuto non ha effetto. Limiti: chi rispedisce da un altro indirizzo un annuncio vero lo fa comparire accanto all'originale (la connessione fallisce la verifica d'identità); se l'orologio di un PC torna indietro, chi lo ha sentito negli ultimi 10 minuti ne ignora gli annunci fino a quando l'orologio recupera. Le versioni precedenti non si vedono con questa. Se non funzionano si usa l'IP o il nome host inserito a mano.
- **Trasporto (implementato, 2026-11-24):** canale tipo Noise su TCP con libsodium: chiavi effimere X25519 firmate con l'identità Ed25519, secretstream XChaCha20-Poly1305. Dettagli e motivazione in [PLAN.md](PLAN.md), "Scostamenti dal piano".
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
