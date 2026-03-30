# Observability Lab

Stack completo de observabilidad y patrones de arquitectura distribuida.

## Requisitos

- Docker y Docker Compose
- .NET SDK 9.0
- Git

## Arrancar
```bash
./setup.sh
```

## Parar
```bash
./teardown.sh             # conserva los datos
./teardown.sh --volumes   # elimina también los datos
```

## Servicios

| Servicio        | URL                        | Descripción                        |
|-----------------|----------------------------|------------------------------------|
| **Gateway**     | **http://localhost**       | **Punto de entrada único**         |
| ApiService      | http://localhost:5068      | API principal (acceso directo)     |
| ProviderService | http://localhost:5069      | Proveedores (acceso directo)       |
| Prometheus      | http://localhost:9090      | Métricas                           |
| Grafana         | http://localhost:3000      | Dashboards y alertas (admin/admin) |
| Jaeger          | http://localhost:16686     | Trazas distribuidas                |
| Loki            | http://localhost:3100      | Logs                               |
| RabbitMQ UI     | http://localhost:15672     | Colas (admin/admin)                |
| PostgreSQL      | localhost:5432             | Base de datos (admin/admin)        |
| Redis           | localhost:6379             | Caché                              |

## Arquitectura
```
Cliente
  ↓
Gateway (puerto 80) — YARP
  ├── /api/*       → ApiService:8080
  └── /providers/* → ProviderService:8080

ApiService
  ├── GET /packages → Redis caché → ProviderService (paralelo)
  ├── POST /bookings → RabbitMQ → WorkerService → PostgreSQL
  └── GET /bookings/{id} → PostgreSQL

WorkerService
  ├── Consume booking-requests
  ├── Circuit breaker → ProviderService
  ├── Retry → booking-requests.retry (TTL 10s)
  └── Dead Letter Queue → booking-requests.dlq (máx 3 reintentos)
```

## Patrones implementados

| Patrón | Dónde | Para qué |
|--------|-------|----------|
| API Gateway | YARP | Punto de entrada único |
| Llamadas paralelas | `/packages` | Reducir latencia |
| Circuit breaker | WorkerService | Proteger contra fallos en cascada |
| Retry + DLQ | RabbitMQ | Mensajes que fallan repetidamente |
| Caché TTL | Redis | Evitar llamadas repetidas |
| Invalidación activa | ProviderService → Redis | Datos frescos al cambiar precios |
| Stale-while-revalidate | ApiService | Latencia 0 aunque expire el caché |
| Observabilidad completa | Prometheus + Grafana + Jaeger + Loki | Las tres señales |

## Endpoints a través del Gateway
```bash
# ApiService
GET  http://localhost/api/fast
GET  http://localhost/api/slow
GET  http://localhost/api/error
GET  http://localhost/api/packages?destination=Mallorca
POST http://localhost/api/bookings
GET  http://localhost/api/bookings/{id}
GET  http://localhost/api/bookings

# ProviderService
GET  http://localhost/providers/availability
GET  http://localhost/providers/availability/slow
POST http://localhost/providers/availability/slow/fail
POST http://localhost/providers/availability/slow/recover
POST http://localhost/providers/prices/update
POST http://localhost/providers/prices/update/all
```

## Simulaciones de fallos

### Caída de servicio
```bash
docker stop $(docker ps -q --filter name=providerservice)
docker start $(docker ps -aq --filter name=providerservice)
```

### Degradación progresiva
```bash
curl http://localhost/providers/degrade  # +200ms por llamada
curl http://localhost/providers/reset    # resetea
```

### Fallos 503
```bash
curl -X POST http://localhost/providers/availability/slow/fail
curl -X POST http://localhost/providers/availability/slow/recover
```

### Memory leak
```bash
curl http://localhost/api/leak        # +1MB
curl http://localhost/api/leak/reset  # libera
```

## Stack técnico

- **API Gateway**: YARP (Yet Another Reverse Proxy)
- **Métricas**: Prometheus + prometheus-net + Grafana
- **Trazas**: OpenTelemetry + Jaeger
- **Logs**: Loki + Promtail + Grafana
- **Servicios**: .NET 9 + ASP.NET Core
- **Mensajería**: RabbitMQ + retry + DLQ
- **Caché**: Redis + StackExchange.Redis
- **Base de datos**: PostgreSQL + Npgsql
- **Resiliencia**: Polly (circuit breaker)
- **Infraestructura**: Docker Compose

## Rate Limiting

El gateway aplica dos límites por IP:

| Límite | Valor | Aplica a |
|--------|-------|----------|
| Burst | 10 req/s | Todos los endpoints |
| Bookings | 5 req/min | POST /api/bookings |

Cuando se supera el límite la respuesta es `429 Too Many Requests` con header
`Retry-After: 60`.

## Autenticación

Todos los endpoints del gateway requieren un token JWT.

### Obtener token
```bash
curl -X POST http://localhost/auth/login \
  -H "Content-Type: application/json" \
  -d '{"username":"user","password":"user123"}'
```

### Usar token
```bash
TOKEN=$(curl -s -X POST http://localhost/auth/login \
  -H "Content-Type: application/json" \
  -d '{"username":"user","password":"user123"}' | python3 -c "import sys,json; print(json.load(sys.stdin)['token'])")

curl http://localhost/api/fast -H "Authorization: Bearer $TOKEN"
```

### Usuarios disponibles
| Usuario | Contraseña | Rol |
|---------|-----------|-----|
| user | user123 | user |
| admin | admin123 | admin |
