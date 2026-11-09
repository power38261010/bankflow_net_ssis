# Practicas senior .NET en BankFlow

El objetivo es probar decisiones y garantias, no incorporar cada API o patron por cumplir una lista. Target actual: .NET 8 / C# 12. No se ha actualizado a C# 13; esa version debe planificarse junto con SDK, runtime y CI.

## Evidencia Implementada

| Area | Evidencia | Limite |
| --- | --- | --- |
| Bajo overhead | OtpInput valida seis caracteres con ReadOnlySpan<char>, sin substring, LINQ ni regex | Es un ref struct usado sincronicamente; no cruza awaits |
| Source generators | GeneratedRegex en BankFlow.Benchmarks compara regex generada con Span y regex normal | Comparacion de benchmark, no infraestructura generica inventada |
| Recursos | await using en transacciones y conexiones; MfaMetrics.Dispose libera Meter; factories cierran bases de pruebas | No se poseen handles nativos: finalizador/SafeHandle no se justifican aqui |
| Async | I/O asincrona, CancellationToken en operaciones bancarias, Task para servicios, ValueTask en filtros/interceptores | No async void, Result, Wait ni Task.Run para envolver consultas |
| Concurrencia | Tokens EF de saldo/propietario; indice idempotente; concurrency stamp Identity; Interlocked y TaskCompletionSource en pruebas | Un lock local no protege dinero entre replicas |
| C# moderno | Records DTO, primary constructors, pattern matching, nullable y global usings del SDK | Sin cambiar contratos solo por usar una novedad |
| Generics y expresiones | IEndpointFilter, ValidationFilter<T>, Identity y RuleFor con expresiones tipadas | Varianza no se agrega donde las interfaces invariantes ya encajan |
| Reflexion | Registro de validadores por assembly, limitado al arranque | No descubrir/reflejar reglas en cada peticion |
| SOLID/DI | TransferService depende de ITransferUnitOfWork; proveedor TOTP es estrategia/decorador de Identity | Scoped para DbContext/servicios de usuario; singleton para TimeProvider/Meter; ValidateScopes y ValidateOnBuild activados |
| Arquitectura | Dominio, casos de uso, infraestructura y API; dos ADR para decisiones | Monolito por capas, no microservicios ni CQRS completo |
| ASP.NET Core | Middleware auth/authorization/rate limit/CSRF, filtros y Minimal APIs por recurso | La cookie pendiente MFA no se usa como autenticacion bancaria |
| EF Core | AsNoTracking, proyecciones/paginas en SQL, filtro de propietario, agregado global e indices validados con plan real | Rutas antiguas y clientes siguen sin paginar; medir antes de Dapper o consultas compiladas |
| Seguridad | Cookie HttpOnly/Strict, CSRF, politicas/claims, TOTP, recovery unica, fallo cerrado | Pendientes MFA obligatorio, passkeys, recuperacion administrativa y cifrado de secretos en reposo |
| Pruebas | WebApplicationFactory, SQLite, SQL Server real, carreras forzadas, fallos inyectados y E2E | SQL tests requieren conexion local explicita; no hay Testcontainers/k6 configurados |
| Diagnostico | Logging estructurado de transferencias y Meter de resultados MFA | No hay exporter OpenTelemetry, Prometheus ni alertas operativas instalados |

## Async, Contexto y Maquina de Estados

El compilador transforma async/await en una maquina de estados que conserva estado entre suspensiones. Task representa la finalizacion; no implica un hilo dedicado. ASP.NET Core no instala por defecto el SynchronizationContext de una UI, pero bloquear con Result/Wait aun puede agotar el Thread Pool. No se cambian schedulers para llamadas de red o EF.

ValueTask se usa en contratos que ya lo requieren, no como sustituto universal de Task. Debe consumirse una sola vez y no almacenarse para multiples awaits. Si un trabajo futuro es CPU-intensivo, medir presupuesto y usar paralelismo acotado fuera del DbContext compartido; no usar Parallel.ForEach ni PLINQ con operaciones EF de la misma sesion.

SemaphoreSlim, Channel<T>, lock y Monitor quedan para problemas reales de coordinacion local. Un worker futuro puede usar Channel<T> acotado con backpressure, pero una cola en memoria no garantiza entrega tras reinicio. Para notificaciones fiables posteriores al commit, evaluar outbox durable y consumidores idempotentes.

## Memoria y Recursos

ReadOnlySpan<T> ya es un ref struct con restricciones de vida. Memory<T> sera apropiado para buffers que deben sobrevivir a awaits, por ejemplo exportacion/importacion por streaming. No agregar un ref struct propio, ArrayPool ni ObjectPool para un codigo de seis caracteres: el coste adicional puede superar el beneficio.

Si una futura libreria entrega handles nativos, usar SafeHandle y Dispose determinista; solo un propietario directo de recursos nativos requiere evaluar finalizacion. DI dispone sus propios singletons y scopes: no disponer manualmente un DbContext compartido ni capturarlo en un singleton.

## Benchmark Reproducible

```bash
dotnet run --project BankFlow.Benchmarks --configuration Release -- --filter '*OtpValidationBenchmarks*'
```

MemoryDiagnoser y ShortRun estan configurados. Una medicion local en Ubuntu/.NET 8.0.22 produjo aproximadamente 4,2 ns Span, 55,1 ns GeneratedRegex y 130,9 ns Regex, sin asignaciones por operacion en los tres casos calientes. Son tres muestras, una entrada constante y una CPU compartida: hay incertidumbre amplia y no prueban el rendimiento global del login. No afirmar reduccion de GC para toda la aplicacion a partir de esto.

Los artefactos se escriben en BenchmarkDotNet.Artifacts del directorio de ejecucion, excluidos de Git y Docker. Medir entradas validas/invalidas, cold start, volumen y percentiles de extremo a extremo antes de una decision de produccion.

## Diagnostico Operativo

Instalar dotnet-counters/dotnet-trace/dotnet-dump mediante el gestor de herramientas del SDK compatible y usar:

```bash
dotnet-counters ps
dotnet-counters monitor --process-id <PID_API> --counters System.Runtime,BankFlow.Security
dotnet-trace collect --process-id <PID_API>
dotnet-dump collect --process-id <PID_API>
```

No se han instalado ni ejecutado esos tres diagnosticos como parte del benchmark. Traces/dumps pueden contener datos privados, claves o contrasenas: generar solo en entornos autorizados, limitar acceso y retencion. Mirar CPU, tasa de asignacion, GC, heap, threads, cola del Thread Pool, latencia SQL y errores antes de optimizar. No ejecutar carga contra datos bancarios reales.

## Siguientes Etapas

1. Ampliar la [medicion local de lecturas ya realizada](performance/read-pages.md) a carga HTTP y escrituras antes de adoptar Dapper, compilacion de consultas o Redis.
2. OpenTelemetry con exporter y presupuesto de cardinalidad, health checks de API y alertas; Meter solo no equivale a monitorizacion desplegada.
3. CI con tests, analisis de dependencias, migraciones controladas y secretos privados; adaptar SQL tests a Testcontainers cuando exista una necesidad reproducible de infraestructura efimera.
4. Outbox/notificaciones antes de incorporar un broker. RabbitMQ, Kafka o Azure Service Bus se elegirian por garantias y operacion, no por lista de tecnologias.
5. OIDC/OAuth2 con un proveedor de identidad o passkeys si hace falta federacion/resistencia a phishing. No agregar JWT a una sesion web por cookies sin un consumidor que lo necesite.
6. gRPC, SignalR/WebSockets y microservicios solo ante requisitos concretos. Documentar carga, contrato, fallos y observabilidad antes de dividir despliegues.