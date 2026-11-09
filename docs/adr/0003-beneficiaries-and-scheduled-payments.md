# ADR 0003: Beneficiarios y pagos programados durables

Estado: aceptado. Fecha: 2026-10-08.

## Alcance

Beneficiarios personales con alias y cuenta interna; pagos unicos, semanales o mensuales en EUR. No hay pagos externos, IBAN verificado, tarifas ni integracion con redes bancarias.

Un cliente programa desde una cuenta propia. Un operador puede crear un mandato para una cuenta asignada a un cliente; el registro conserva propietario y creador. El beneficiario seleccionado siempre pertenece al creador, incluso si este es operador. La lista de pagos se limita al propietario para clientes y permite vista global a operadores. Los beneficiarios no tienen un bypass de lectura administrativa.

## Snapshot y Calendario

El mandato guarda el nombre/cuenta destino y el ID del beneficiario. Archivar un beneficiario solo lo oculta para nuevos pagos; no cambia ni cancela mandatos existentes. Origen y destino deben existir, tener propietario y ser distintos. El importe es positivo, hasta 1.000.000, con dos decimales.

La fecha inicial debe estar en el futuro, como maximo un anio desde la creacion. La UI recibe fecha/hora local y la convierte a UTC. El calendario recurrente conserva hora UTC: no implementa zonas IANA ni ajustes de DST. Semanal suma siete dias; mensual conserva el dia ancla (31 pasa al ultimo dia de febrero y vuelve al 31 cuando el mes lo permite).

Si el servicio estuvo detenido, ejecuta una vez el vencimiento pendiente y avanza al primer vencimiento futuro. No cobra automaticamente todas las ocurrencias omitidas. Una ejecucion exitosa genera la clave nueva de la siguiente ocurrencia. La fecha de reintento es independiente de la fecha programada para no desplazar la mensualidad.

No se reserva saldo al programar. La comprobacion ocurre al ejecutar. Saldo insuficiente, cuentas no disponibles o autorizacion revocada dejan el plan en Failed; no se reintentan automaticamente ni se continua su recurrencia. El usuario debe revisar y crear un mandato nuevo si corresponde.

## Idempotencia y Atomicidad

POST /scheduled-payments requiere Idempotency-Key UUID. El indice unico CreatedByUserId + CreationKey devuelve el mismo mandato ante reintentos; la misma clave con distinto contenido devuelve conflicto. FirstRunAt y BeneficiaryId permiten comparar la peticion original aunque el plan ya haya avanzado. La UI conserva una programacion incierta en sessionStorage por usuario y reintenta la misma clave/payload tras recargar.

Cada vencimiento tiene otro UUID persistente, OccurrenceKey, empleado por TransferService bajo el propietario del origen. La clave no cambia durante una recuperacion de lease o reintento temporal. Los debitos, creditos, movimientos y el estado del plan se guardan mediante un unico SaveChanges dentro de la transaccion EF; un fallo revierte el conjunto. El decorador ScheduledTransferStore agrega la finalizacion al commit del caso de uso existente, sin duplicar reglas de transferencia.

El indice de transferencias ActorUserId + IdempotencyKey sigue arbitrando carreras. Si ya existe comprobante se concilia el estado sin otro debito. No hay una promesa de exactly-once para solicitudes que el usuario envie deliberadamente con claves distintas.

## Worker y Leases

BackgroundService despierta cada cinco segundos y procesa como maximo diez candidatos por ciclo. Cada intento tiene un scope asincrono propio, con DbContext y UserManager scoped; el singleton del worker solo retiene IServiceScopeFactory, reloj y logger.

El claim usa un lease de dos minutos, GUID de lease y Version como concurrency token. Dos workers pueden leer un candidato, pero solo una actualizacion con la version original gana. Un claim obsoleto no puede confirmar ni cancelar el trabajo de otro. Tras reinicio, un lease vencido es recuperable.

Antes de ejecutar se comprueba que propietario y creador existan, no esten bloqueados y conserven los roles permitidos. La propiedad del origen y disponibilidad del destino se vuelven a verificar por TransferService. Identity y SQL Server no comparten transaccion: una revocacion simultanea posterior a esa comprobacion es una limitacion, no una garantia de autorizacion distribuida instantanea.

Fallos temporales/conflictos de saldo se reintentan a un minuto, con maximo cinco intentos registrados. Un corte brusco deja el lease pendiente de recuperar. No se mantienen transacciones SQL abiertas entre peticiones ni durante la espera del calendario.

## Cancelacion

Pending se puede cancelar. Processing solo cuando su lease ha vencido; la API calcula CanCancel y aun revalida en el comando para evitar carreras con la pantalla. Completed/Cancelled/Failed no se cancelan ni se editan. Cancelar cambia Version, por lo que una ejecucion que haya leido el estado anterior no puede guardar un debito aislado si pierde esa carrera.

## Persistencia y Operacion

Beneficiaries y ScheduledPayments estan en SQL Server. Las fechas de estos nuevos agregados se persisten como ticks UTC (bigint) para comparacion/indexacion consistente con SQLite de pruebas; la API publica DateTimeOffset ISO 8601. Indices de propietario, estado/NextAttemptAt y claves unicas soportan listados y polling.

Payments:WorkerEnabled=false permite desactivar el worker en tests de API o despliegues dedicados. Por defecto esta activo y necesita una instancia de API siempre disponible. App Service con suspension/scale-to-zero retrasa los vencimientos; para produccion evaluar un worker dedicado, cuotas y monitorizacion.

La UI muestra estados y permite actualizar; no hay SignalR, SMS ni correo. LastTransferId correlaciona con movimientos/comprobante. Los logs del worker incluyen identificadores y tipo de fallo, no nombres, saldos ni conceptos.

## Verificacion

Pruebas de dominio: fechas, lease, cancelacion, mes corto y saltos tras inactividad. API: beneficiarios privados, origen propio, duplicados, replay de creacion, conflicto de payload y cancelacion. SQL Server real: reclamo concurrente, lease recuperado, cobro unico, commit conjunto, fallo inyectado de credito y autorizacion revocada. E2E desktop/mobile: alta, perdida de respuesta al programar, recarga/replay, ejecucion real, saldo y cancelacion.

## Proxima Etapa

Notificaciones fiables mediante un outbox persistido en el mismo commit que transferencia/estado, consumidor idempotente y SignalR como canal de presentacion. No enviar una notificacion antes de confirmar SQL. Esa etapa no se ha implementado aqui.

Faltan limites por usuario, aprobacion reforzada/step-up de mandatos, reservas y conciliacion regulatoria, auditoria inmutable y politica de festivos/zonas horarias. Esta demo no esta certificada para dinero real.