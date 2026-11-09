# ADR 0002: MFA TOTP voluntario

Estado: aceptado. Fecha: 2026-10-08.

## Decision

Implementar TOTP con app autenticadora y activacion voluntaria por usuario. La decision se confirmo con el usuario frente a SMS con Twilio Verify. No se ha implementado SMS ni contratado un proveedor. SMS puede evaluarse posteriormente, considerando SIM swapping, coste y verificacion del numero; passkeys ofrecen una alternativa resistente al phishing.

Usar ASP.NET Core Identity para claves, validacion TOTP, bloqueo y codigos de recuperacion. No implementar HMAC-TOTP ni el protocolo de autenticacion desde cero. QR se genera localmente con qrcode; OTP.NET y OTPAuth solo generan codigos en pruebas.

## Flujo

1. La sesion existente y la contrasena actual permiten iniciar setup; MFA activo impide reconfigurar silenciosamente la clave.
2. La clave y el URI otpauth solo se devuelven a ese usuario autenticado, sin cache. Un codigo valido confirma el alta.
3. Activar MFA genera diez codigos de recuperacion, cambia el security stamp y vuelve a autenticar la sesion actual. Las otras sesiones y desafios anteriores quedan invalidados.
4. En el login, la contrasena correcta de un usuario con MFA devuelve 202, sin cookie de aplicacion nueva. La cookie HttpOnly BankFlow.MfaPending contiene identidad y security stamp, expira a los cinco minutos y no concede permisos bancarios.
5. La verificacion valida el stamp, estado MFA y bloqueo, y utiliza el segundo factor de Identity. Solo entonces se crea la sesion completa, con claim amr=mfa. No se recuerda el dispositivo para omitir MFA en futuros logins.
6. Desactivar o regenerar recuperacion exige contrasena y TOTP o codigo de recuperacion. La regeneracion invalida los codigos anteriores. Desactivar cambia stamp y cierra la sesion.

## Replay y Concurrencia

ReplayProtectedAuthenticatorProvider delega la validacion del algoritmo en Identity. Guarda una marca HMAC del codigo y clave, no el codigo en claro, en los tokens Identity. Rechaza un uso repetido durante 180 segundos, por encima de la ventana de aceptacion TOTP nativa. El concurrency stamp de Identity y la clave primaria de sus tokens arbitran peticiones simultaneas. Una colision de persistencia falla cerrada.

Las marcas vencidas se limpian al aceptar otro codigo. Los cambios de configuracion son transacciones locales de Identity; el primer factor y la verificacion del login usan las operaciones nativas y no mantienen transacciones abiertas entre peticiones. Las pruebas fuerzan una carrera con conexiones SQLite independientes.

La ventana de uso nativa exige relojes correctos en servidor y autenticador. El codigo usado para activar MFA no sirve otra vez inmediatamente: usar el siguiente codigo generado o uno de recuperacion. No admitir codigos cortos, Unicode numerico ni separadores ambiguos en la API.

## Operacion

Limites por defecto: diez logins y treinta peticiones MFA por minuto; el bloqueo de cuenta se produce tras cinco errores y dura quince minutos. Los limites IP en memoria no sustituyen un limitador distribuido. Las excepciones de volumen E2E son opciones explicitas solo en Development.

MfaMetrics publica bankflow.mfa.events en el Meter BankFlow.Security con etiquetas de resultado acotadas, sin usuarios, claves ni codigos. Meter es singleton propiedad de DI e implementa IDisposable; no hay recursos nativos propios ni motivo para un finalizador.

## Limites

Identity conserva su almacen SQLite y sus formatos nativos de claves/codigos. Esta implementacion no cifra adicionalmente esos secretos en la base: proteger fichero, copias y disco, o usar almacenamiento cifrado y un gestor de claves antes de produccion. El QR y la descarga de recuperacion son secretos: no registrar ni compartir capturas. La UI no los conserva en localStorage/sessionStorage y los elimina del estado al cerrar el componente; un archivo descargado sigue siendo responsabilidad del usuario.

No existe recuperacion administrativa por correo/SMS si se pierden autenticador y todos los codigos. No habilitar MFA sin guardar recuperacion. La politica voluntaria no impone step-up por transferencia ni MFA obligatorio a operadores. MFA TOTP no elimina phishing, malware de navegador ni robo de una sesion completa.

Antes de produccion: migraciones Identity versionadas, TLS, claves de Data Protection protegidas, recuperacion verificada, auditoria durable, limitacion distribuida y pruebas de carga. No presentar este flujo como una certificacion bancaria.