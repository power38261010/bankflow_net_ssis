# Lecturas paginadas: evidencia local

Fecha: 2026-10-08. Escenario de laboratorio, no prueba de capacidad de produccion.

## Contratos

- GET /accounts/page?page=1&pageSize=20&search=texto devuelve items, page, pageSize y totalCount.
- GET /accounts/summary devuelve accountCount y totalBalance de todas las cuentas autorizadas, independiente de busqueda/pagina.
- GET /accounts/{id}/transactions/page?page=1&pageSize=20 devuelve una pagina de movimientos y sus metadatos.
- Page entre 1 y 10000, pageSize entre 1 y 100, busqueda de hasta 100 caracteres. Valores invalidos dan 400; cuenta ajena/inexistente da 404.
- Las rutas antiguas /accounts y /accounts/{id}/transactions permanecen por compatibilidad, sin paginar. Angular ya usa las rutas nuevas.

IBankingReadQueries separa lectura y escritura. SqlBankingReadQueries filtra en SQL por propietario, proyecta records antes de materializar y no devuelve entidades rastreadas. No hace JOIN ni Include; estas consultas no necesitan cargar relaciones.

Orden: CreatedAt DESC, Id DESC. El segundo campo desempata de forma determinista. La paginacion es offset, no cursor; inserciones entre peticiones pueden desplazar filas y count/items no son una instantanea transaccional comun. Para historiales grandes o paginas profundas evaluar keyset e aislamiento apropiado. El limite de pagina no garantiza que un offset alto sea barato.

La busqueda substring es parametrizada y su sensibilidad depende de la collation. Un indice no convierte Contains en busqueda sargable por prefijo; medir filtros grandes y considerar prefijo/full-text cuando corresponda.

## SQL Verificado

SqlReadQueriesTests usa SQL Server real y cuenta comandos etiquetados:

| Camino | Comandos bancarios | Resultado |
| --- | ---: | --- |
| Pagina de cuentas | 2 | count e items, independiente de 10 o 25 filas |
| Resumen | 1 | COUNT/SUM en SQL |
| Movimientos autorizados | 3 | existencia/propiedad, count e items |
| Cuenta ajena | 1 | rechazo sin consultar movimientos |

Se comprueban OFFSET/FETCH, filtro, ausencia de JOIN y ChangeTracker vacio. Esto demuestra ausencia de N+1 en estos caminos, no una auditoria de Identity ni toda la aplicacion.

Plan real de items de movimientos antes: Top, Filter, Sort, Clustered Index Scan. Tras AddReadPageIndexes: Top, Index Seek. Se eliminaron Sort y escaneo en este escenario.

Indices agregados:

- Accounts(OwnerUserId, CreatedAt, Id), INCLUDE CustomerName, AccountNumber, Balance.
- Accounts(CreatedAt, Id), INCLUDE CustomerName, AccountNumber, Balance, OwnerUserId.
- Transactions(AccountId, CreatedAt, Id), INCLUDE Amount, Type, Description, TransferId.

Los indices estrechos anteriores se conservan para counts/FKs. Los nuevos consumen espacio y encarecen escrituras: validar mantenimiento y volumen real. El plan depende de estadisticas/selectividad y no usa hints.

## Carga Medida

Ubuntu/.NET 8.0.22, Release, SQL Server 2022 Docker local. Base creada/eliminada por ejecucion, 5001 cuentas, 2000 movimientos, 100 operaciones por escenario, concurrencia 4 y una operacion de calentamiento. Cada operacion tiene DbContext propio y serializa JSON.

| Escenario posterior a indices | p50 ms | p95 ms | Bytes asignados en 100 ops | Gen 0 / 1 / 2 |
| --- | ---: | ---: | ---: | --- |
| Cuentas completas | 81,37 | 97,29 | 418930432 | 61 / 29 / 11 |
| Cuentas pagina 20 | 5,55 | 7,36 | 12749208 | 2 / 0 / 0 |
| Movimientos completos | 35,24 | 44,57 | 218226960 | 33 / 25 / 10 |
| Movimientos pagina 20 | 5,28 | 7,83 | 14748072 | 3 / 0 / 0 |

Antes de indices, las paginas tuvieron p95 22,92 ms en cuentas y 11,92 ms en movimientos. Son ejecuciones separadas, sin repeticion estadistica ni control completo del host. El cambio de plan es evidencia estructural; la diferencia temporal no se atribuye solo al indice.

La comparacion limita la cantidad devuelta: no es el mismo trabajo mas rapido. Las paginas devuelven veinte filas; las rutas completas miles. No incluye login, Identity, middleware, red HTTP, UI, resumen ni listado de clientes. No inferir RPS maximos, SLA ni mejoras de toda la API.

Asignaciones y colecciones son diferencias de proceso por intervalo, no medidas exactas por request. Heap/LOH muestreados pueden conservar estado de fases anteriores; no diagnostican leaks. La serializacion completa puede producir buffers grandes en LOH.

## Contadores Reales

Se capturo System.Runtime con dotnet-counters 8.0.547301. Dieciseis muestras de un segundo: cola Thread Pool observada 0, hilos 3..11, tasa de asignacion maxima ~224 MB/s, heap hasta ~48 MB y fragmentacion ~0,24..20,13 %. El muestreador interno a 100 ms vio colas transitorias maximas de 1.

No hubo cola sostenida observada bajo esta carga; no prueba ausencia de starvation con otra carga. Los contadores incluyen arranque, migraciones, calentamiento y cancelacion; excepciones del runtime no equivalen a lecturas fallidas. Las lecturas terminaron y el escenario falla ante errores SQL.

## Reproduccion

Solo servidor local/de pruebas con permisos para crear bases; nunca produccion:

```bash
read -r -s -p 'Contrasena SQL Server: ' SQLSERVER_PASSWORD
echo
export BANKFLOW_TEST_SQL="Server=localhost,1433;Database=master;User Id=sa;Password=$SQLSERVER_PASSWORD;TrustServerCertificate=True"
unset SQLSERVER_PASSWORD
dotnet test BankFlow.Tests --filter FullyQualifiedName~SqlReadQueriesTests
export BANKFLOW_LOAD_REPORT=/tmp/bankflow-read-report
dotnet run --project BankFlow.Benchmarks --configuration Release -- --read-load
```

Genera read-load.json, movement-page.sql y movement-page.sqlplan y elimina solo BankFlow_ReadLoad_{guid}. Artefactos de esta medicion: /tmp/bankflow-read-before, /tmp/bankflow-read-after y /tmp/bankflow-read-runtime.csv. Son temporales, no versionados; la tabla conserva el resumen.

Capturar el proceso hijo, no el compilador/SDK:

```bash
dotnet tool install dotnet-counters --tool-path /tmp/bankflow-diagnostics --version '8.*'
dotnet build BankFlow.Benchmarks --configuration Release
/tmp/bankflow-diagnostics/dotnet-counters collect --counters System.Runtime --format csv --output /tmp/bankflow-read-runtime.csv -- dotnet BankFlow.Benchmarks/bin/Release/net8.0/BankFlow.Benchmarks.dll --read-load
```

SQL/planes/trazas reales pueden contener datos privados. Los capturados aqui son sinteticos. No subir dumps, credenciales ni planes con parametros personales al repositorio.

## Diagnostico Siguiente

Para starvation correlacionar cola sostenida, hilos, latencia y stacks con dotnet-trace. Para retencion comparar ventanas similares y analizar tipos/GC roots de un dump autorizado. No se ha provocado starvation, creado un leak ni analizado dumps en este trabajo.

Siguientes gates: carga HTTP con mezcla de endpoints, concurrencias mayores, frio/caliente, percentiles repetidos, offsets, coste de indices en escrituras y paginas de clientes. No agregar ArrayPool, consultas compiladas, cache o Dapper sin medir el cuello de botella restante.