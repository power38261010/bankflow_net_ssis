# ADR 0001: Transferencias internas atomicas e idempotentes

Estado: aceptado. Fecha: 2026-10-07.

## Contexto

BankFlow tiene una API ASP.NET Core, casos de uso en Application, entidades Domain y persistencia EF Core en Infrastructure. Todas las cuentas y movimientos estan en un unico SQL Server; Identity usa otra base. Las transferencias deben conservar el total de dinero, resistir escrituras concurrentes y permitir reintentos tras perder una respuesta.

## Decision

Mantener un monolito por capas y un caso de uso vertical TransferService, sin introducir microservicios, MediatR, Sagas ni Event Sourcing. La nueva funcionalidad no tiene efectos externos ni necesita transacciones distribuidas.

La API decide el actor y su rol a partir de Identity; nunca los recibe del cuerpo de la peticion. Application valida la propiedad de la cuenta origen y coordina el caso de uso. Domain valida el importe y mueve fondos entre las cuentas. ITransferUnitOfWork delimita la persistencia; SqlTransferUnitOfWork carga y guarda todas las entidades en un mismo DbContext.

## Garantias

- Un solo SaveChangesAsync confirma el debito, el credito, dos movimientos correlacionados y el comprobante. EF Core abre una transaccion SQL implicita que cubre todos los comandos; un fallo revierte el conjunto.
- Balance y OwnerUserId son tokens de concurrencia. Los UPDATE condicionados al valor original evitan sobrescribir otra operacion. Esta proteccion tambien cubre depositos y retiros existentes.
- El indice unico ActorUserId + IdempotencyKey arbitra peticiones duplicadas entre procesos o replicas, sin depender de un cache en memoria.
- Antes de ejecutar, se busca el comprobante existente. Un replay con los mismos datos devuelve 200 y el comprobante original; cambiar cuenta, importe o concepto con la misma clave devuelve 409.
- Ante un conflicto en commit, se descarta el estado local y se consulta el comprobante persistido. Si otra peticion igual gano la carrera se devuelve su resultado; de lo contrario, un conflicto de saldo o un deadlock revertido devuelve balance_changed.
- No se reintentan mutaciones automaticamente con nuevas claves. Un resultado de red incierto se recupera usando la misma clave y el mismo contenido.
- Numeros de cuenta unicos evitan un destino ambiguo. Un destinatario debe existir y tener propietario. No se expone su titular ni su saldo.
- El cliente solo puede debitar una cuenta propia; el operador puede operar cuentas asignadas. La lectura de comprobantes se permite al propietario del origen o al operador.
- El registro Transfer conserva actor, fecha y referencia junto a los movimientos. Los logs operativos no incluyen contrasenas, conceptos ni saldos.

## Cliente Web

Angular exige revision y confirmacion antes de enviar. Guarda una unica peticion pendiente por usuario en sessionStorage, con su UUID, origen, destino, importe y concepto. No guarda credenciales. Si falta una respuesta, mantiene el contenido inmutable incluso tras recargar o volver a iniciar sesion.

Una peticion anteriormente incierta solo se libera tras recuperar un comprobante: un 401/403 no demuestra que el intento anterior no se haya ejecutado. No se debe borrar manualmente la clave ni fabricar una nueva para repetir una operacion incierta. Si la incidencia no puede resolverse con el mismo reintento, requiere conciliacion de un operador.

Tras un exito, recarga saldos e historial. El saldo mostrado puede quedar desactualizado si esa consulta falla; el comprobante sigue siendo el resultado definitivo de la transferencia. La descarga actual es JSON, no un PDF firmado.

## Verificacion

Pruebas de API y persistencia cubren permisos, importes, saldo insuficiente, replay, claves incompatibles, movimientos pareados y rollback mediante un fallo inyectado. Pruebas SQL Server crean bases desechables y sincronizan dos peticiones justo antes de commit para forzar carreras reales. E2E de escritorio y movil pierden deliberadamente la respuesta despues del commit, recargan, simulan caducidad de sesion y comprueban que se recupera el mismo comprobante sin otro debito.

## Consecuencias y Limites

La garantia es una unica ejecucion exitosa por usuario y clave, no exactly-once para intenciones que el cliente envie con claves distintas. Solo se persisten transferencias exitosas; no hay reservas, cancelaciones ni estado pendiente en el servidor. Los deadlocks o cambios de saldo requieren un reintento consciente; un conflicto no autoriza un saldo negativo.

Los movimientos pareados no constituyen por si solos un libro mayor contable completo. Falta conciliacion, auditoria inmutable, controles regulatorios, autenticacion reforzada de operaciones y limites operativos por usuario. Las pruebas no certifican la plataforma para dinero real.

Si aparecen notificaciones o integraciones externas, evaluar un outbox transaccional y consumidores idempotentes; no publicarlos antes de confirmar SQL. Si los modulos se separan fisicamente, revisar las garantias en vez de asumir que esta unidad de trabajo cubre bases distintas.