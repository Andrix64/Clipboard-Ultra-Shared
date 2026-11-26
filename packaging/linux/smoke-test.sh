#!/usr/bin/env bash
# Prova di avvio di un pacchetto: CUS gira per qualche secondo su un display virtuale (Xvfb) con un
# portachiavi Secret Service usa e getta. Passa se alla scadenza è ancora in esecuzione e ha creato il
# profilo con il database cifrato, cioè se librerie, runtime e portachiavi funzionano.
#
# Uso: packaging/linux/smoke-test.sh <comando che avvia CUS>
# Richiede xvfb-run, dbus-run-session e gnome-keyring-daemon.
set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "Uso: $0 <comando che avvia CUS>" >&2
  exit 2
fi
command=$1

home=$(mktemp -d)
trap 'rm -rf "$home"' EXIT
export HOME="$home" XDG_DATA_HOME="$home/.local/share"

dbus-run-session -- bash -uo pipefail -c '
  printf ci | gnome-keyring-daemon --unlock --components=secrets > /dev/null
  xvfb-run -a timeout 20 "$1" --profile smoke --clipboard simulated --no-discovery --port 23799
  status=$?
  if [[ $status -ne 124 ]]; then
    echo "CUS si è chiuso da solo (codice $status) invece di restare aperto." >&2
    exit 1
  fi
' _ "$command"

if [[ ! -f "$XDG_DATA_HOME/CUS/profiles/smoke/cus.db" ]]; then
  echo "CUS non ha creato il profilo: l'avvio non è arrivato al database." >&2
  exit 1
fi
echo "Avvio riuscito: $command"
