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
| ApiService      | http://localhost:5068      | API principal                      |
| ProviderService | http://localhost:5069      | Simulador de proveedores externos  |
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
ApiService (puerto 5068)
  ├── GET /packages → Redis caché → ProviderService (paralelo)
  ├── POST /bookings → RabbitMQ → WorkerService → PostgreSQL
  └── GET /bookings/{id} → PostgreSQL

WorkerService
  ├── Consume booking-requests
  ├── Circuit breaker en llamadas a ProviderService
  ├── Retry con cola booking-requests.retry (TTL 10s)
  └── Dead Letter Queue booking-requests.dlq (máx 3 reintentos)

ProviderService (puerto 5069)
  ├── GET /availability → proveedor rápido (50-300ms)
  └── GET /availability/slow → proveedor lento (1000-3000ms)
```

## Patrones implementados

| Patrón | Dónde | Para qué |
|--------|-------|----------|
| Llamadas paralelas | `/packages` | Reducir latencia de N a max(N) |
| Circuit breaker | WorkerService → ProviderService | Proteger contra fallos en cascada |
| Retry + DLQ | RabbitMQ | Mensajes que fallan repetidamente |
| Caché TTL | Redis | Evitar llamadas repetidas a proveedores |
| Invalidación activa | ProviderService → Redis | Datos frescos cuando cambian precios |
| Stale-while-revalidate | ApiService | Latencia 0 aunque el caché expire |
| Observabilidad completa | Prometheus + Grafana + Jaeger + Loki | Las tres señales |

## Endpoints ApiService
```bash
# Endpoints básicos
GET  /fast                    → respuesta rápida
GET  /slow                    → respuesta lenta (200-800ms)
GET  /error                   → falla el 50% de las veces
GET  /packages?destination=X  → búsqueda con caché (TTL + stale)

# Reservas
POST /bookings                → crea reserva asíncrona
GET  /bookings/{id}           → consulta estado de reserva
GET  /bookings                → lista últimas 20 reservas

# Simulaciones
GET  /leak                    → añade 1MB de memoria
GET  /leak/reset              → libera la memoria
```

## Endpoints ProviderService
```bash
GET  /availability            → proveedor rápido
GET  /availability/slow       → proveedor lento

POST /availability/slow/fail    → activa fallos 503
POST /availability/slow/recover → recupera el proveedor
GET  /degrade                   → añade 200ms de latencia
GET  /reset                     → resetea latencia y fallos

POST /prices/update             → invalida caché de un destino
POST /prices/update/all         → invalida todo el caché
```

## Simulaciones de fallos

### Caída de servicio
```bash
docker stop $(docker ps -q --filter name=providerservice)
docker start $(docker ps -aq --filter name=providerservice)
```

### Degradación progresiva
```bash
curl http://localhost:5069/degrade  # +200ms por llamada
curl http://localhost:5069/reset    # resetea
```

### Fallos 503
```bash
curl -X POST http://localhost:5069/availability/slow/fail
curl -X POST http://localhost:5069/availability/slow/recover
```

### Memory leak
```bash
curl http://localhost:5068/leak        # +1MB
curl http://localhost:5068/leak/reset  # libera
```

### Dead Letter Queue
```bash
# Activa fallos para que los mensajes vayan a DLQ tras 3 reintentos
curl -X POST http://localhost:5069/availability/slow/fail

# Envía una reserva
curl -X POST http://localhost:5068/bookings \
  -H "Content-Type: application/json" \
  -d '{"bookingId":"","destination":"Test","passengers":1}'

# Ver estado en BD
curl http://localhost:5068/bookings
```

## Stack técnico

- **Métricas**: Prometheus + prometheus-net + Grafana
- **Trazas**: OpenTelemetry + Jaeger
- **Logs**: Loki + Promtail + Grafana
- **Servicios**: .NET 9 + ASP.NET Core
- **Mensajería**: RabbitMQ + patron retry + DLQ
- **Caché**: Redis + StackExchange.Redis
- **Base de datos**: PostgreSQL + Npgsql
- **Resiliencia**: Polly (circuit breaker)
- **Infraestructura**: Docker Compose
