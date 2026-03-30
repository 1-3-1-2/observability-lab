# Decisiones de arquitectura — Observability Lab

Este documento explica qué hay implementado, por qué se eligió cada tecnología
y qué problema resuelve cada patrón. Es un documento vivo — se actualiza cada
vez que se añade algo nuevo.

---

## Infraestructura base

### Docker Compose
Usamos Docker Compose porque necesitamos levantar múltiples servicios coordinados
en local de forma reproducible. Cualquier persona puede clonar el repo y tener
el entorno idéntico con `./setup.sh`. Sin Docker, cada desarrollador tendría que
instalar y configurar manualmente RabbitMQ, PostgreSQL, Redis, Prometheus, etc.

### WSL2 en Windows
Los contenedores Docker corren en Linux. En Windows, WSL2 proporciona un kernel
Linux real donde Docker funciona con rendimiento nativo. Sin WSL2, Docker en
Windows usa una VM más lenta y tiene problemas de compatibilidad con volúmenes.

---

## Servicios propios

### ApiService (.NET 9)
El servicio principal que recibe todas las peticiones externas. Actúa como
orquestador — decide si consultar proveedores directamente, usar caché, o
delegar el procesamiento a una cola. Expone los endpoints de búsqueda y reservas.

### ProviderService (.NET 9)
Simula integraciones con proveedores externos de contenido turístico (Amadeus,
hoteles, etc.). En producción real sería un adaptador hacia APIs externas. Lo
tenemos separado del ApiService para poder degradarlo, fallar y recuperarlo de
forma independiente — igual que ocurre con proveedores reales.

### WorkerService (.NET 9)
Proceso en segundo plano que consume mensajes de RabbitMQ. Lo tenemos separado
del ApiService porque el procesamiento de reservas es lento (consulta proveedores,
guarda en BD) y no queremos bloquear las peticiones HTTP. Si el worker cae, las
reservas siguen entrando en la cola — no se pierden.

---

## Mensajería

### RabbitMQ
Usamos RabbitMQ como broker de mensajes para el procesamiento asíncrono de reservas.
Cuando un cliente crea una reserva, la API responde inmediatamente con un ID y
publica un mensaje en la cola. El WorkerService lo procesa en segundo plano.

Sin RabbitMQ, la API tendría que esperar a que los proveedores respondan antes de
responder al cliente — si hay 1000 reservas simultáneas, 1000 conexiones HTTP
quedarían abiertas esperando. Con la cola, la API responde en milisegundos siempre.

**Patrón retry implementado:** si el procesamiento falla, el mensaje va a una cola
`booking-requests.retry` con TTL de 10 segundos. Después vuelve automáticamente
a la cola principal para reintentarse. Máximo 3 intentos.

**Dead Letter Queue (DLQ):** después de 3 fallos, el mensaje va a
`booking-requests.dlq` para inspección manual. Evita que un mensaje corrupto
bloquee el worker indefinidamente. El estado en BD cambia a `dead_lettered`.

---

## Base de datos

### PostgreSQL
Usamos PostgreSQL para persistir el estado de las reservas. Cada reserva tiene
un ciclo de vida: `processing → completed / retrying → dead_lettered`. El cliente
puede consultar el estado en cualquier momento con `GET /bookings/{id}`.

Elegimos PostgreSQL sobre otras opciones porque es el estándar de facto para
datos transaccionales en sistemas de reservas, tiene excelente soporte en .NET
con Npgsql, y sus métricas son exportables a Prometheus con postgres-exporter.

---

## Caché

### Redis
Usamos Redis para cachear las respuestas de los proveedores. Consultar
disponibilidad a un proveedor tarda entre 1 y 3 segundos. Si 100 usuarios
buscan paquetes a Mallorca en el mismo minuto, sin caché harían 100 llamadas
a los proveedores. Con caché, solo la primera va al proveedor — las otras 99
responden en <10ms desde Redis.

**Tres estrategias implementadas:**

1. **TTL simple (30s):** la respuesta se guarda 30 segundos. Pasado ese tiempo,
   la siguiente petición va al proveedor y renueva el caché. Simple y efectivo
   para datos que cambian poco.

2. **Invalidación activa:** cuando el proveedor actualiza precios, llama a
   `POST /prices/update` que borra la clave en Redis inmediatamente. La siguiente
   petición obtiene datos frescos. Útil cuando sabemos exactamente cuándo cambian
   los datos.

3. **Stale-while-revalidate:** guardamos dos claves por destino — una fresca
   (TTL 30s) y una stale (TTL 60s). Cuando la fresca expira, servimos la stale
   inmediatamente y refrescamos en background. El usuario nunca espera más de
   10ms, aunque el caché haya expirado. Es la estrategia de mayor disponibilidad.

---

## Resiliencia

### Circuit Breaker (Polly)
Implementado en el WorkerService para proteger las llamadas a ProviderService.

Sin circuit breaker: si un proveedor está caído, cada llamada espera el timeout
completo (ej. 30s) antes de fallar. Con 100 reservas en cola, el worker está
bloqueado 50 minutos en timeouts inútiles.

Con circuit breaker: después de 3 fallos consecutivos, el CB se abre y las
llamadas fallan inmediatamente sin intentarlo. Después de 30s pasa a half-open
y prueba una llamada. Si funciona, se cierra. El sistema se recupera solo.

**Configuración:** FailureRatio=0.5, MinimumThroughput=3, BreakDuration=30s.

### Llamadas en paralelo (Task.WhenAll)
El endpoint `/packages` consulta dos proveedores. Originalmente eran secuenciales:
tiempo_total = tiempo_fast + tiempo_slow (~3s). Con Task.WhenAll se lanzan en
paralelo: tiempo_total = max(tiempo_fast, tiempo_slow) (~2s). Un cambio de
una línea que reduce la latencia un 30%.

---

## Observabilidad

Implementamos las tres señales de observabilidad recomendadas por OpenTelemetry:

### Métricas — Prometheus + Grafana
Prometheus scrapeea métricas de todos los servicios cada 15 segundos. Grafana
las visualiza en dashboards. Las métricas clave son latencia p99 (no la media —
la media miente), tasa de errores, y cache hit ratio.

Usamos percentil 99 (p99) porque la media oculta outliers. Si 99 requests
tardan 10ms y 1 tarda 10s, la media es ~110ms pero el p99 es 10s. Sin p99,
ese usuario invisible sufre en silencio.

### Trazas — OpenTelemetry + Jaeger
Cada request genera una traza con spans para cada operación. Cuando ApiService
llama a ProviderService, el trace_id se propaga en las cabeceras HTTP. Jaeger
reconstruye el recorrido completo mostrando exactamente dónde se pierde el tiempo.

Sin trazas, cuando `/packages` tarda 3s no sabes si el problema es en tu código,
en la llamada al proveedor rápido, o en el lento. Con trazas, lo ves en segundos.

### Logs — Loki + Promtail
Promtail recoge los logs de todos los contenedores y los envía a Loki. Grafana
los visualiza. Los logs son estructurados en JSON con campos consistentes,
incluyendo el trace_id para correlacionarlos con las trazas.

### Alertas como código
Las alertas están definidas en `docker/grafana-alerts.yml` y se cargan
automáticamente al arrancar Grafana. No se pierden entre reinicios. La alerta
principal dispara cuando el p99 de latencia supera 1 segundo.

---

## Pendiente de implementar

- **API Gateway (YARP)** — punto de entrada único para todos los servicios
- **Kubernetes** — orquestación de contenedores en producción
- **Outbox pattern** — garantía de publicación de eventos
- **Rate limiting** — protección contra abuso

## API Gateway

### YARP (Yet Another Reverse Proxy)
Usamos YARP como API Gateway porque es el proxy inverso oficial de Microsoft
para .NET, con configuración declarativa en JSON y sin código adicional.

Sin gateway, el cliente necesita conocer la URL de cada servicio y gestionar
el enrutamiento él mismo. Cuando los servicios escalan o cambian de IP, hay
que actualizar todos los clientes. Con el gateway, el cliente siempre habla
con `http://localhost` y el gateway decide a dónde va cada petición.

**Rutas configuradas:**
- `/api/*` → ApiService — elimina el prefijo `/api` antes de reenviar
- `/providers/*` → ProviderService — elimina el prefijo `/providers`

El gateway también es el lugar natural para añadir en el futuro:
autenticación centralizada, rate limiting, logging de acceso, y SSL termination.

## Rate Limiting

### Implementado en el Gateway (YARP)
El rate limiting está en el gateway porque es el punto de entrada único — así
proteges todos los servicios con una sola configuración sin tocar cada servicio.

**Dos niveles de protección:**

1. **Burst limit (10 req/s por IP):** evita que un cliente sature el sistema
   con un pico súbito de peticiones. Aunque tenga 60 peticiones/minuto
   disponibles, no puede enviarlas todas en 1 segundo.

2. **Bookings limit (5 req/min por IP):** el endpoint POST /bookings es el más
   costoso — crea un mensaje en RabbitMQ, el worker consulta proveedores y
   guarda en PostgreSQL. Limitamos a 5 por minuto para protegerlo.

**Respuesta al superar el límite:** HTTP 429 con header `Retry-After: 60`
indicando al cliente cuándo puede volver a intentarlo.

**Particionado por IP:** cada IP tiene su propio contador independiente.
Un cliente abusivo no afecta a los demás.

## Autenticación

### JWT en el Gateway
Implementamos autenticación JWT en el gateway porque es el punto de entrada
único — así todos los servicios quedan protegidos sin tocar su código.

**Flujo:**
1. Cliente hace POST /auth/login con usuario y contraseña
2. Gateway devuelve un JWT firmado con HMAC-SHA256 (válido 1 hora)
3. Cliente incluye el token en cada petición: `Authorization: Bearer <token>`
4. Gateway valida la firma, expiración e issuer antes de dejar pasar la petición
5. Si el token es inválido o no existe → 401 Unauthorized

**Claims del token:**
- `name` — nombre del usuario
- `role` — rol (admin/user)
- `jti` — ID único del token (para revocación futura)
- `iat` — timestamp de creación

**El gateway propaga el usuario a los servicios downstream** via headers
`X-User` y `X-User-Role` — así los servicios pueden saber quién hace la
petición sin validar el token de nuevo.

**Usuarios de ejemplo:**
- `user / user123` — rol user
- `admin / admin123` — rol admin

**Nota de producción:** la clave secreta debe estar en variables de entorno
o un vault (HashiCorp Vault, Azure Key Vault). Los usuarios deben estar en
BD con passwords hasheados con bcrypt.
