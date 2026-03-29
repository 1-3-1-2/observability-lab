# Observability Lab

Stack completo de observabilidad para aprender métricas, trazas y logs
en un entorno de microservicios real.

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

## Simulaciones de fallos

### Caída de servicio
```bash
docker stop $(docker ps -q --filter name=providerservice)
docker start $(docker ps -aq --filter name=providerservice)
```

### Degradación progresiva
```bash
# Añade 200ms de latencia por llamada
curl http://localhost:5069/degrade

# Resetea
curl http://localhost:5069/reset
```

### Memory leak
```bash
# Añade 1MB por llamada
curl http://localhost:5068/leak

# Libera memoria
curl http://localhost:5068/leak/reset
```

## Stack técnico

- **Métricas**: Prometheus + prometheus-net + Grafana
- **Trazas**: OpenTelemetry + Jaeger
- **Logs**: Loki + Promtail + Grafana
- **Servicios**: .NET 9 + ASP.NET Core
- **Infraestructura**: Docker Compose
