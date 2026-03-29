#!/bin/bash

# ============================================================
# teardown.sh — para y limpia el stack completo
# Uso: ./teardown.sh
# Flags: --volumes  elimina también los datos persistentes
# ============================================================

set -e

echo "==== Parando Observability Lab ===="
echo ""

cd docker

if [ "$1" == "--volumes" ]; then
    echo "Parando contenedores y eliminando volúmenes de datos..."
    docker compose down --volumes --remove-orphans
    echo "OK: Contenedores parados y datos eliminados"
else
    echo "Parando contenedores (los datos se conservan)..."
    docker compose down --remove-orphans
    echo "OK: Contenedores parados"
    echo ""
    echo "Tip: usa './teardown.sh --volumes' para eliminar también los datos"
fi

echo ""
echo "==== Stack parado ===="
