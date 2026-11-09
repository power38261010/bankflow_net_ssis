# BankFlow

Workspace bancario para operadores y clientes: ASP.NET Core 8, SQL Server y Angular 21 con TypeScript, Angular Material/CDK y formularios reactivos.

## Funcionalidades

- Login privado, recuperacion de sesion al recargar y logout.
- MFA TOTP voluntario con QR local, bloqueo, caducidad y codigos de recuperacion de un solo uso.
- Listado y busqueda de cuentas, alta, depositos, retiros e historial.
- Cuentas y movimientos paginados en servidor, resumen global y consultas proyectadas sin tracking.
- Alta privada de clientes por operadores, cuentas vinculadas a su propietario y vista personal de consulta.
- Asignacion de cuentas antiguas sin propietario, sin cambiar titulares ya asignados.
- Transferencias internas atomicas, confirmacion, comprobantes JSON y reintentos idempotentes.
- Bandeja de notificaciones persistente y avisos de transferencias en tiempo real mediante SignalR.
- Beneficiarios personales y pagos unicos/semanales/mensuales con worker durable, cancelacion y creacion idempotente.
- Confirmacion de operaciones, estados de carga, errores y disenio adaptable.
- Importes presentados en EUR. El dominio existente no modela monedas ni conversiones.

## Autenticacion

ASP.NET Core Identity guarda usuarios y hashes PBKDF2 en SQLite, separada del SQL Server bancario. No existe registro publico ni usuario predeterminado. El JWT anterior y sus claves fijas fueron retirados: `/auth/login` ahora devuelve `204` y una cookie, no un token JSON. Los clientes anteriores deben adaptarse.

- Contrasenas: minimo 12 caracteres, mayuscula, minuscula, numero, simbolo y 4 caracteres distintos.
- Bloqueo durante 15 minutos tras 5 intentos fallidos.
- Limite de 10 peticiones de login por minuto y direccion IP; respuesta `429` al excederlo.
- Cookie HttpOnly, SameSite Strict, Secure fuera de Development, sesion de 30 minutos sin renovacion deslizante.
- Validacion CSRF en todas las escrituras, incluido login; Angular envia `X-XSRF-TOKEN` automaticamente.
- Logout invalida el security stamp y todas las sesiones de ese usuario. Se verifica en cada peticion.
- Angular no guarda credenciales ni tokens en localStorage/sessionStorage.

El usuario configurado por bootstrap se crea solo si no existe; cambiar Bootstrap:Password no cambia su contrasena. Recibe el rol Operator. Un bootstrap anterior sin rol se actualiza a Operator una sola vez, invalidando sus sesiones para que vuelva a iniciar sesion. No se promueven usuarios que ya tengan otro rol. Retira la variable de bootstrap despues de provisionarlo.

## Roles y Propietarios

| Accion | Operator | Client |
| --- | --- | --- |
| Consultar cuentas y movimientos | Todas | Solo propias |
| Crear clientes | Si | No |
| Crear cuentas y seleccionar propietario | Si | No |
| Asignar propietario a una cuenta antigua | Si, solo sin propietario | No |
| Registrar depositos y retiros de caja | Si | No |
| Transferir entre cuentas asignadas | Cualquier origen asignado | Solo origen propio |
| Guardar beneficiarios | Directorio personal | Directorio personal |
| Programar/cancelar pagos | Cuentas asignadas / vista global | Solo propias |

El operador crea clientes con el boton Nuevo cliente, su correo y una contrasena inicial que cumpla la politica. No puede elegir el rol: la API asigna siempre Client. El cliente entra por el mismo login y ve Mis cuentas. Los depositos/retiros actuales representan operaciones de caja; las transferencias permiten al cliente mover fondos desde sus propias cuentas.

POST /accounts requiere ownerUserId de un cliente registrado ademas de customerName y accountNumber. PUT /accounts/{id}/owner asigna una cuenta antigua sin propietario. Las cuentas con OwnerUserId nulo no aparecen a clientes. El control se aplica en la API, no solo ocultando botones. Una consulta a una cuenta ajena devuelve 404; las acciones fuera del rol devuelven 403.

Los identificadores de Identity se guardan como propietarios en SQL Server. No hay una clave foranea entre las dos bases: el backend valida que el usuario exista y tenga rol Client antes de crear o asignar la cuenta. Esta etapa no incluye eliminacion de usuarios ni cambios de rol de clientes.

## Migraciones Bancarias

SQL Server usa migraciones EF Core versionadas: InitialBanking, AddAccountOwnership, AddInternalTransfers, AddReadPageIndexes, AddBeneficiariesAndScheduledPayments, AddScheduleCreationIdempotency y AddTransactionalNotificationOutbox. En desarrollo se aplican al arrancar la API. Agregan propietarios, transferencias, indices, beneficiarios, mandatos y bandejas; si hay numeros de cuenta duplicados, hay que resolverlos antes de migrar, no se borran automaticamente.

Para una base creada anteriormente con EnsureCreated, el inicializador reconoce las tablas Accounts y Transactions sin historial, registra la migracion inicial como baseline y aplica la columna nullable OwnerUserId y su indice. Conserva cuentas, saldos y movimientos; no asigna propietarios automaticamente. Usa el boton Asignar propietario en cada cuenta antigua que deba pertenecer a un cliente.

El baseline solo sirve para el esquema anterior de este proyecto; las bases con tablas incompletas se rechazan. Haz una copia antes de actualizar una base importante. En produccion, sustituye la aplicacion de migraciones al arrancar por un despliegue controlado, sin multiples replicas migrando simultaneamente. Identity conserva EnsureCreated en SQLite por ahora.

## Docker (desarrollo)

1. Crea tu configuracion local a partir del ejemplo:

```bash
cp .env.example .env
```

2. Completa `BOOTSTRAP_EMAIL`, `BOOTSTRAP_PASSWORD` y `MSSQL_SA_PASSWORD` en `.env` con valores propios y unicos. El archivo esta excluido de Git. No compartas su contenido.

3. Arranca:

```bash
docker compose up --build -d
```

Frontend: http://localhost:4200. API: http://localhost:8080. Swagger: http://localhost:8080/swagger.

Nginx sirve Angular y reenvia `/api/*` a la API bajo el mismo origen. Los volumenes `auth_data`, `data_protection_keys` y `sqlserver_data` conservan usuarios, claves de cookies y cuentas.

Este Compose es exclusivamente local: usa Development y HTTP. No lo expongas a Internet. La API espera al healthcheck de SQL Server antes de arrancar. Deten los servidores locales de Angular/API antes de usar Compose si ocupan sus puertos.

## Desarrollo sin Docker para la aplicacion

Requisitos: .NET SDK 8, Node 22.12+ LTS y npm. SQL Server debe estar disponible; en Linux no se puede usar el LocalDB de appsettings.

```bash
docker compose up -d sqlserver
export ASPNETCORE_ENVIRONMENT=Development
read -r -s -p 'Contrasena SQL Server: ' MSSQL_SA_PASSWORD
echo
export ConnectionStrings__BankFlowDb="Server=localhost,1433;Database=BankFlow;User Id=sa;Password=$MSSQL_SA_PASSWORD;TrustServerCertificate=True"
export Bootstrap__Email='operador@example.com'
read -r -s -p 'Contrasena inicial: ' Bootstrap__Password
echo
export Bootstrap__Password
dotnet run --project BankFlow.Api --no-launch-profile --urls http://127.0.0.1:5080
```

En otro terminal:

```bash
cd BankFlow.Web
npm ci
npm start -- --host 127.0.0.1 --port 4200
```

Abre http://127.0.0.1:4200. El proxy Angular apunta a `127.0.0.1:5080`. Identity usa `bankflow-auth.db` en el directorio de contenido de la API; puedes cambiar `ConnectionStrings__AuthDb`. No cambies entre localhost y 127.0.0.1 durante una sesion.

Si tu Node global no es compatible, ejecuta `npx --yes --package=node@22 -c 'npm start -- --host 127.0.0.1 --port 4200'` desde BankFlow.Web; lo mismo aplica a npm ci, build y test.

## Arquitectura

Decision de arquitectura y garantias: [ADR 0001](docs/adr/0001-internal-transfers.md).
Consultas y medicion real: [evidencia de paginacion y carga](docs/performance/read-pages.md).
Beneficiarios y calendario: [ADR 0003](docs/adr/0003-beneficiaries-and-scheduled-payments.md).
Outbox transaccional y SignalR: [ADR 0004](docs/adr/0004-transactional-notification-outbox.md).

## Beneficiarios y Pagos

Entra en Pagos desde el menu. Guarda un beneficiario con alias y una cuenta BankFlow asignada. Programar pago selecciona origen, beneficiario, importe, fecha/hora local y frecuencia (unico/semanal/mensual). La agenda muestra fechas en UTC. Tambien puedes programar desde la cuenta seleccionada en su historial.

Programar no reserva saldo. El worker intenta el cobro al vencer y se ejecuta dentro de la API mientras esta activa. Pendiente puede cancelarse; un pago con ejecucion activa no. Si el lease vence, el servidor vuelve a permitir cancelacion. Saldo insuficiente deja Failed y detiene la recurrencia. Actualizar pagos recupera el estado mas reciente.

Archivar/eliminar un beneficiario no cancela los pagos existentes, que conservan su destino. No editar ni borrar manualmente una programacion incierta: reintentarla con la misma clave o conciliar antes de crear otra. Las claves de creacion y de cada vencimiento son persistentes y diferentes.

API: GET/POST /beneficiaries, GET/DELETE /beneficiaries/{id}, GET/POST /scheduled-payments, GET /scheduled-payments/{id} y POST /scheduled-payments/{id}/cancel. Los listados aceptan page/pageSize. POST de programacion exige Idempotency-Key UUID; un replay devuelve el mismo ID, no otro mandato. Todas las escrituras requieren CSRF.

El worker esta activo por defecto; `Payments__WorkerEnabled=false` lo desactiva. Pruebas WebApplicationFactory lo desactivan y prueban el runner con reloj controlado; E2E usan el worker real y bases desechables. Requiere SQL Server y una instancia siempre disponible para ejecucion puntual. Las notificaciones de transferencia se guardan en outbox/bandeja; SignalR da aviso en vivo y HTTP permite recuperarlas tras desconexion. No hay SMS/email ni notificaciones de pagos programados.

## Notificaciones

La transferencia y sus mensajes de notificacion se confirman en el mismo commit SQL. El worker publica a grupos SignalR privados, separados por usuario y security stamp, y la UI vuelve a leer la bandeja; el evento realtime solo contiene el ID. Cada notificacion persistida se deduplica por mensaje de outbox. Si SignalR falla, se reintenta con backoff; si se pierde la conexion, la bandeja conserva el aviso.

La interfaz muestra transferencias enviadas/recibidas, importe y ultimos cuatro digitos de la contraparte, nunca su cuenta completa. El menu marca lecturas en servidor. El proxy de desarrollo usa `/hubs/**`; Nginx Docker configura WebSocket. `Notifications__WorkerEnabled=false` desactiva la publicacion realtime; la transferencia sigue guardando los registros de outbox y estos se procesan al reactivar el worker.

En una sola instancia la API inicia un worker. En multiples replicas la bandeja sigue compartida por SQL, pero los grupos SignalR son locales: usar Azure SignalR Service o backplane Redis para avisos inmediatos entre replicas. Sin backplane, HTTP sigue recuperando las notificaciones. Define retencion/archivado antes de produccion: outbox e inbox no se purgan automaticamente.

La bandeja se consulta con `GET /notifications?page=1&pageSize=20`; se marca leida con `POST /notifications/read` y el ID. Ambas rutas comprueban el usuario propietario y las escrituras requieren CSRF. El hub es `/hubs/notifications`; el navegador vuelve a consultar la bandeja al recibir un ID, reconectar o cada 30 segundos. SignalR transporta identificadores, nunca el payload bancario.

Los mensajes y las dos notificaciones quedan en el mismo commit que la transferencia. El worker confirma primero la bandeja persistente y despues SignalR; si el realtime falla, reintenta con backoff. La entrega puede repetirse entre server y navegador, por eso la bandeja se deduplica por outbox ID y el cliente reemplaza su estado al releerla. La UI solo muestra importe, tipo y ultimos cuatro digitos.

SQL Server usa `AddTransactionalNotificationOutbox`; las fechas del outbox e inbox se almacenan como ticks UTC (`bigint`) para conservar el orden/comparacion uniforme en SQL Server y SQLite.
MFA: [ADR 0002](docs/adr/0002-voluntary-totp-mfa.md). Evidencia y plan: [practicas senior .NET](docs/senior-dotnet-practices.md).

## Activar MFA

Abre Seguridad de acceso (icono de escudo), Activar MFA y confirma la contrasena. Registra el QR o la clave en Microsoft/Google Authenticator u otra app TOTP, y confirma con sus seis digitos. Guarda los diez codigos de recuperacion en un lugar privado: solo se muestran al generarlos. El codigo empleado para activar ya esta consumido; para el siguiente login espera un nuevo codigo o usa uno de recuperacion.

El login con MFA responde 202 antes del segundo factor y no da acceso a cuentas. El desafio dura cinco minutos. No hay SMS: se eligio TOTP sin proveedor externo. MFA es voluntario para clientes y operadores; tu cuenta no se activa automaticamente. Renovar recuperacion y desactivar MFA exigen contrasena y segundo factor, e invalidan sesiones anteriores. Si pierdes autenticador y recuperacion no hay bypass administrativo implementado.

Las claves/codigos siguen el almacenamiento Identity nativo; antes de produccion protege SQLite, backups y disco con cifrado adecuado. No compartas QR, clave, codigos, capturas ni dumps. TOTP no sustituye passkeys ni verificacion reforzada por operacion.

## API de Transferencias

POST /transfers requiere sesion, CSRF y cabecera Idempotency-Key con un UUID. El cuerpo contiene sourceAccountId, destinationAccountNumber, amount y description opcional. El importe va de 0,01 a 1.000.000 EUR, con hasta dos decimales; origen y destino deben ser distintos y tener propietario.

Una creacion responde 201 con comprobante y Location; la repeticion exacta responde 200 con el mismo comprobante. Una clave reutilizada con otros datos responde 409/idempotency_conflict. Saldo insuficiente y conflictos de concurrencia responden 409 con codigo propio. GET /transfers/{id} permite consultar el comprobante al propietario del origen o al operador.

Ambos saldos, los dos movimientos y el comprobante se guardan juntos. La UI conserva en sessionStorage el contenido de una peticion incierta para reintentarla con la misma clave, incluso tras volver a iniciar sesion. Ese contenido incluye datos bancarios, pero no contrasenas ni tokens de acceso: no uses un navegador compartido ni borres una operacion incierta sin conciliarla.

## Pruebas

```bash
dotnet test BankFlow.Tests/BankFlow.Tests.csproj
cd BankFlow.Web
npm run build
npx playwright install chromium
npm test
```

Los E2E necesitan frontend y API en marcha. Sin `E2E_EMAIL` y `E2E_PASSWORD`, solo ejecutan login/validacion y omiten el flujo autenticado. Para comprobarlo, usa un usuario de pruebas en una base desechable:

```bash
export E2E_URL=http://127.0.0.1:4200
export E2E_EMAIL='tu-operador-de-pruebas@example.com'
read -r -s -p 'Contrasena de pruebas: ' E2E_PASSWORD
echo
export E2E_PASSWORD
npm test
```

Las pruebas necesitan credenciales de un operador. Crean clientes y cuentas con prefijo E2E, depositan, retiran, comprueban aislamiento, transferencia, perdida de respuesta despues del commit, recarga, caducidad de sesion y replay sin doble debito. Generan capturas en `BankFlow.Web/test-results`. No ejecutarlas contra datos de produccion. Las pruebas .NET por defecto usan SQLite en memoria; las tres pruebas SQL Server se omiten si BANKFLOW_TEST_SQL no esta definida.

Los E2E MFA crean usuarios temporales, generan TOTP mediante OTPAuth, verifican QR no vacio y prueban recuperacion/renovacion/desactivacion. Para una API desechable Development puedes definir RateLimits__LoginPermitLimit=100 y RateLimits__MfaPermitLimit=100, evitando que los logins de toda la suite superen el limite normal. No poner esos valores en el entorno habitual: por defecto son 10 y 30; fuera de Development no se amplian. Reinicia ng serve despues de instalar dependencias si Vite recarga la primera vista lazy durante sus optimizaciones.

Para verificar carreras reales y rollback en SQL Server local:

```bash
read -r -s -p 'Contrasena SQL Server: ' MSSQL_SA_PASSWORD
echo
export BANKFLOW_TEST_SQL="Server=localhost,1433;Database=master;User Id=sa;Password=$MSSQL_SA_PASSWORD;TrustServerCertificate=True"
dotnet test BankFlow.Tests/BankFlow.Tests.csproj
```

Se crean y eliminan exclusivamente bases con nombre BankFlow_TransferTests_{guid}. La conexion necesita permisos para crear bases. No apuntar este entorno de pruebas a un servidor de produccion.

## Limites Antes de Produccion

Esta es una base funcional de portfolio, no una plataforma bancaria certificada. Los operadores acceden a todas las cuentas; los clientes consultan las propias y pueden transferir desde ellas.

- Implementar invitaciones con enlace de activacion, cambio obligatorio de contrasena inicial y recuperacion de contrasena con verificacion de correo. TOTP voluntario ya esta implementado; evaluar MFA obligatorio/passkeys y recuperacion verificada. El alta actual con contrasena elegida por el operador es solo para la demo local.
- Configurar HTTPS en el proxy y API, forwarded headers solo de proxies confiables, CSP compatible y secretos mediante un gestor seguro. Rotar las credenciales SQL locales y usar un usuario SQL de privilegios minimos.
- Persistir y proteger las claves de Data Protection; cifrarlas en reposo. Compartir claves entre replicas y sustituir el limitador en memoria por uno distribuido.
- Versionar el esquema Identity, preparar un plan de copias y usar almacenamiento Identity apropiado para multiples replicas; las migraciones bancarias ya estan versionadas.
- Preparar conciliacion, auditoria inmutable, limites por usuario y autenticacion reforzada de transferencias antes de manejar dinero real. La atomicidad, idempotencia y concurrencia actuales tienen pruebas, pero no sustituyen una certificacion bancaria.
- Ampliar mediciones HTTP y paginas profundas, paginar clientes y evaluar mantenimiento de indices con escrituras; cuentas e historial ya tienen rutas paginadas.
