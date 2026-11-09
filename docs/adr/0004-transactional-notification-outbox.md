# ADR 0004: Outbox transaccional y notificaciones SignalR

Estado: aceptado. Fecha: 2026-10-08.

## Decision

Guardar eventos de transferencia y su bandeja en SQL Server. Solo SignalR es transitorio. No añadir un broker ni prometer entrega exactly-once al navegador.

SqlTransferUnitOfWork añade dos mensajes de outbox, uno por propietario, a la misma unidad EF que persiste saldos, movimientos y transferencia. El origen recibe TransferSent; el destino recibe TransferReceived. Si ambos pertenecen al mismo usuario se crea un único aviso. El payload incluye referencia, importe, fecha y los ultimos cuatro digitos de la contraparte; nunca el numero completo, nombre, saldo, concepto, email ni token.

## Entrega

NotificationOutboxWorker usa IServiceScopeFactory y crea un scope/DbContext por lote. SqlOutboxPublisher reclama cada fila mediante lease y concurrency token. En una transaccion SQL crea el registro duradero Notification, deduplicado por OutboxMessageId, y marca ProcessedAt. Tras el commit publica solamente el ID a un grupo SignalR derivado de ClaimTypes.NameIdentifier. Un sink fallido conserva RealtimeSentAt nulo y aplica backoff de 15 segundos; una entrega confirmada marca RealtimeSentAt. Lease dura un minuto y se recupera tras reinicios.

Si SignalR acepta el evento y el proceso cae antes de persistir RealtimeSentAt, puede volver a publicarse. Por eso el evento solo avisa que hay cambios; el cliente vuelve a leer la bandeja por HTTP y reemplaza su pagina por IDs, evitando aplicar saldos/notificaciones duplicados. La durabilidad de la bandeja es la garantia; SignalR no es un registro de auditoria ni garantiza que el navegador estaba conectado.

El worker corre en la API cada dos segundos, hasta cincuenta registros por lote. Hay un solo worker habilitado por defecto, pero leases/concurrency permiten mas instancias. SignalR usa backplane en memoria en la configuracion actual: la bandeja SQL se comparte entre replicas, los grupos no. Para varias instancias detras de proxy, configurar Azure SignalR Service o backplane Redis; si no, las bandejas siguen recuperables por HTTP, pero el aviso inmediato solo llega a conexiones de la instancia publicadora.

## Seguridad

El Hub requiere la politica BankingUser, acepta solo Origin exacto de mismo host/esquema y une la conexion a un grupo formado por ID de usuario del servidor. El cliente no envia destinatario ni grupo. La negociacion SignalR de metodos seguros no necesita token CSRF; la cookie de sesion SameSite Strict, autenticacion, Origin y el grupo impiden suscripcion arbitraria. Todas las operaciones de escritura HTTP mantienen CSRF.

Inbox pagina por recipientUserId; marcar leido incluye recipientUserId en el UPDATE. Otra persona recibe 404. Los eventos realtime no llevan contenido, solo un ID de notificacion; se aplica autorizacion de nuevo en HTTP.

## Verificacion y Limites

Pruebas SQL fuerzan fallo de insercion del outbox y confirman rollback de fondos, movimientos y transferencia. Otras prueban bandejas separadas, payload enmascarado y reintento tras fallo del hub sin duplicar Notification. El E2E espera la lectura HTTP causada por SignalR antes del intervalo de polling y verifica persistencia tras reload en desktop y movil.

El outbox y la inbox crecen sin retencion/archivado configurado. No hay selector de preferencias ni notificacion de pago programado en esta entrega; los avisos implementados corresponden solo a transferencias. No se envia email/SMS/push móvil. WorkerEnabled=false omite publicacion realtime; los registros Inbox existentes siguen consultables. SignalR en proceso no es un backplane distribuido.

Antes de produccion definir retencion y borrado, alertas de backlog/lease expirado, destino distribuido SignalR, proxy/TLS/forwarded headers confiables, metricas de entrega y paginacion del usuario. Para correo u otros efectos externos, publicar un evento outbox especifico y usar proveedores/consumidores idempotentes despues del commit.
