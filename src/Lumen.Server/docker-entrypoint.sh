#!/bin/sh
set -eu

# Named volumes are created as root. Fix ownership once at startup, then drop
# privileges so the relay never runs as root.
mkdir -p "${Lumen__DataDir:-/data}"
if [ "$(id -u)" = "0" ]; then
    chown lumen:lumen "${Lumen__DataDir:-/data}" 2>/dev/null || true
fi

if [ "$(id -u)" = "0" ]; then
    exec setpriv --reuid=10001 --regid=10001 --clear-groups --inh-caps=-all "$@"
fi

exec "$@"
