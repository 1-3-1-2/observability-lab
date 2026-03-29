#!/bin/bash

# ============================================================
# setup.sh — levanta el stack completo de observability-lab
# Uso: ./setup.sh
# Requisitos: Docker, Docker Compose, .NET SDK 9.0
# ============================================================

set -e  # Para el script si cualquier comando falla

echo "==== Observability Lab Setup ===="
echo ""

# ---- Verificar requisitos ----
echo "[1/4] Verificando requisitos..."

if ! command -v docker &> /dev/null; then
    echo "ERROR: Docker no está instalado"
    exit 1
fi

if ! command -v dotnet &> /dev/null; then
    echo "ERROR: .NET SDK no está instalado"
    exit 1
fi

if ! command -v git &> /dev/null; then
    echo "ERROR: Git no está instalado"
    exit 1
fi

echo "OK: Docker $(docker --version | cut -d' ' -f3 | tr -d ',')"
echo "OK: .NET $(dotnet --version)"
echo "OK: Git $(git --version | cut -d' ' -f3)"
echo ""

# ---- Restaurar dependencias ----
echo "[2/4] Restaurando dependencias de .NET..."

cd src/ApiService && dotnet restore --verbosity quiet && cd ../..
echo "OK: ApiService"

cd src/ProviderService && dotnet restore --verbosity quiet && cd ../..
echo "OK: ProviderService"
echo ""

# ---- Levantar el stack ----
echo "[3/4] Levantando el stack con Docker Compose..."

cd docker
docker compose down --remove-orphans 2>/dev/null || true
docker compose up -d --build
cd ..
echo ""

# ---- Verificar que todo está UP ----
echo "[4/4] Verificando servicios..."
echo "Esperando 15 segundos para que arranquen los contenedores..."
sleep 15

check_service() {
    local name=$1
    local url=$2
    if curl -s -o /dev/null -w "%{http_code}" "$url" | grep -q "200\|302"; then
        echo "OK: $name -> $url"
    else
        echo "WARN: $name no responde en $url (puede necesitar más tiempo)"
    fi
}

check_service "ApiService     " "http://localhost:5068/fast"
check_service "ProviderService" "http://localhost:5069/availability"
check_service "Prometheus     " "http://localhost:9090"
check_service "Grafana        " "http://localhost:3000"
check_service "Jaeger         " "http://localhost:16686"
check_service "Loki           " "http://localhost:3100/ready"

echo ""
echo "==== Stack levantado ===="
echo ""
echo "URLs disponibles:"
echo "  ApiService:      http://localhost:5068"
echo "  ProviderService: http://localhost:5069"
echo "  Prometheus:      http://localhost:9090"
echo "  Grafana:         http://localhost:3000  (admin/admin)"
echo "  Jaeger:          http://localhost:16686"
echo "  Loki:            http://localhost:3100"
echo ""
echo "Endpoints de simulacion:"
echo "  GET /fast                    -> respuesta rapida"
echo "  GET /slow                    -> respuesta lenta (200-800ms)"
echo "  GET /error                   -> falla el 50% de las veces"
echo "  GET /packages                -> llama a dos proveedores en paralelo"
echo "  GET /leak                    -> añade 1MB de memoria (simula leak)"
echo "  GET /leak/reset              -> libera la memoria"
echo "  GET /degrade (providerservice) -> añade 200ms de latencia"
echo "  GET /reset   (providerservice) -> resetea la latencia"
echo ""
