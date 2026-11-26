# Lingue di CUS

Un file per lingua, chiamato con il codice ISO a due lettere: `it.json`, `en.json`...
Ogni file è un oggetto JSON chiave → testo, più `"$name"` con il nome della lingua scritto nella lingua stessa
(è l'etichetta del pulsante in IMPOSTAZIONI → LINGUA).

I file vengono incorporati nell'assembly di `Cus.Core` (vedi `Cus.Core.csproj`) e letti da `Loc.cs`.

## Aggiungere una lingua

1. Copia `en.json` in `<codice>.json` (per esempio `de.json`) e traduci i valori. Non toccare le chiavi.
2. Lascia identici i segnaposto `{0}`, `{1:N0}`...: puoi spostarli nella frase, ma ognuno deve restare.
   Le graffe letterali nei testi con segnaposto si scrivono `{{` e `}}`.
3. Aggiungi la lingua a `installer/cus.iss`, sezione `[Languages]`, con `Name` uguale al codice
   (per esempio `Name: "de"; MessagesFile: "compiler:Languages\German.isl"`).
4. Esegui `dotnet test tests/Cus.Core.Tests`: i test di `LocTests` controllano che il file abbia
   tutte e sole le chiavi di `en.json` e gli stessi segnaposto.

Le chiavi che mancano in una lingua si prendono da `en.json`.

## Aggiungere un testo

Scegli una chiave `<area>.<nome>` (per esempio `main.offerAsHost`), aggiungila a **tutti** i file e usala:

- in C#: `Loc.T("main.offerAsHost")`, oppure `Loc.T("startup.profile", profile)` con i segnaposto;
- nello XAML: `Text="{ui:Tr main.offerAsHost}"`;
- per i testi mandati all'altro PC: un nuovo `WireText` in `src/Cus.Core/Net/WireText.cs` con chiave `wire.<nome>`.
