# Etapa 4A — Meshy sin consumo de créditos

## Alcance y arquitectura

Implementación local .NET 10, sin paquetes NuGet adicionales, sin Autodesk en Core/Meshy, sin servidor ni base de datos. Proyecto nuevo `src/BimFamilyAI.Meshy`, referenciado por la solución y por un ejecutable de comprobaciones independiente. Revit sigue referenciando únicamente Core: el servicio aún no se expone en su interfaz ni se instala con el complemento.

Core añade `ImageGenerationRequest`, `ProcessingState`, `GenerationProgress` y `GeneratedObjResult`. Este último construye `ObjImportRequest` exigiendo unidades/eje/nombre y altura opcional explícitos. No deduce dimensiones físicas de la foto. `ObjInspection`, `ObjFamilyImporter`, `FamilyFileWorkflow`, los tres comandos y el instalador permanecen sin cambios de código.

Meshy se reparte en `LocalImage` (entrada), `MeshyOptions` (perfil/costos/errores), `MeshyClient` (HTTP/estados), `MeshyDownload` (resultado/caché) y `JsonTaskStore` (diario). Se usan HttpClient, System.Text.Json, SHA256, streams y CancellationToken del framework. No se incorpora una interfaz genérica para proveedores que aún no existen.

## Documentación oficial verificada el 16 de noviembre de 2026

- [Image to 3D](https://docs.meshy.ai/en/api/image-to-3d): POST y GET `/openapi/v1/image-to-3d[/id]`; `image_url` acepta Data URI. JPG/JPEG, PNG, WebP: 20 MB, mínimo 32 por lado, máximo 178956970 píxeles. No se publica otro máximo por lado en esta página. Se adopta 20.000.000 bytes, conservador frente a una interpretación MiB.
- Perfil enviado: `model_type=smart-topology`, `ai_model=meshy-t2`, `should_texture=false`, `target_polycount=4000`, `target_formats=["obj"]`. T2 admite 100–15000 caras. Se omiten remesh, decimation, symmetry, image_enhancement, remove_lighting, textura y estimación automática de tamaño. El conteo es un objetivo, no garantía.
- Modelos vigentes: estándar latest/7.1, 6, 6-lite; Smart Topology T2/T2.5. T2.5 admite hasta 30000 caras. No se usan `lowpoly` retirado ni 7 obsoleto. Las opciones actuales exponen solo el perfil T2 probado; ampliar modelos/calidad/texturas requiere añadir perfiles validados, sin cambiar contratos Core.
- Formatos oficiales: GLB, OBJ, FBX, STL, USDZ y 3MF; se solicita únicamente OBJ.
- [Quickstart](https://docs.meshy.ai/en/api/quick-start): Bearer en Authorization; `MESHY_API_KEY` en entorno. La cuenta necesita condiciones de acceso propias de Meshy; esta entrega no crea claves ni modifica suscripciones.
- [Ciclo](https://docs.meshy.ai/en/api/task-lifecycle): creación 202 con `result`; estados PENDING, IN_PROGRESS, SUCCEEDED, FAILED; CANCELED es actualmente de Creative Lab, pero se interpreta defensivamente. Progreso 0–100. POST 60 s, GET 30 s y seguimiento cada Retry-After o 5 s. No hay timeout global que declare fallida una tarea por esperar: detener localmente conserva el ID, siguiendo la recomendación de permitir al menos 60 minutos.
- [Errores](https://docs.meshy.ai/en/api/errors): clasificación HTTP y `task_error`. GET transitorio (timeout, 429, 5xx) tiene hasta dos reintentos con 5/10 s y espera nunca inferior a Retry-After, incluido formato fecha. POST no se reintenta, aun con 429/5xx o JSON ambiguo.
- [Precios](https://docs.meshy.ai/en/api/pricing): T2 sin texturas indica 5 créditos por tarea en la fecha de consulta. Es una referencia documental, no una compra ni una llamada de generación. El perfil evita texturas y detalle innecesario; el número de caras no se presenta como descuento adicional.
- [Retención](https://docs.meshy.ai/en/api/asset-retention): origen firmado `assets.meshy.ai`, conservación hasta 3 días salvo Enterprise. **Diferencia frente al supuesto de renovar enlaces del prompt:** volver a obtener la tarea devuelve la misma URL y expiración; no garantiza renovación. Se permite una nueva consulta al expirar y después se informa el problema, sin regenerar. La copia propia es necesaria.

## Entrada local

`LocalImage.ReadAsync` limita el archivo antes de asignar memoria, lo abre solo para lectura y conserva el mismo contenido para hash y envío. Comprueba extensión contra firma real, dimensiones, contenedor completo y lectura exacta. PNG comprueba chunks, CRC e IHDR/IDAT/IEND; JPEG recorre marcadores hasta SOF/SOS y exige EOI; WebP inspecciona RIFF y VP8/VP8L/VP8X. Se rechazan WebP animados. Se calcula SHA256 y se prepara Data URI solo en memoria.

Es una inspección estructural/de metadatos, no un decodificador exhaustivo de píxeles comprimidos. Puede existir un JPEG/WebP con contenido comprimido dañado que supere sus cabeceras y sea rechazado después por Meshy. No se promete reparar fotos ni reemplazar la validación del proveedor. No se requiere una librería gráfica externa ni un codec WebP instalado.

## Flujo y uso de las operaciones

1. `CreateSimulated` exige manejadores derivados de `SimulatedMeshyHandler`. No construye un transporte de red. Es la vía inicial y la única usada para solicitudes de las comprobaciones.
2. `CreateAsync(new ImageGenerationRequest(path), options)` valida imagen/opciones, calcula huella imagen+perfil y consulta el diario. Una huella conocida devuelve el ID anterior.
3. Antes del único POST se guarda una reserva `Uncertain`. Con 202/ID válido se guarda `Known` inmediatamente, incluso si se cancela localmente después de recibir el ID. Si falla ese guardado, `MeshyException.TaskId` conserva el identificador recibido para recuperación.
4. `RecoverAsync(id)` registra o recupera un ID conocido sin POST. No necesita la fotografía para consultar/descargar. `QueryAsync` actualiza estado, progreso, fecha remota si está disponible y código seguro. `TrackAsync` informa progreso hasta terminal y acepta CancellationToken. Fallo/cancelación remota termina el seguimiento; no hay cancelación remota implementada.
5. `GetResultAsync(id)` primero verifica el OBJ local (inspección y SHA256). Si existe y es válido devuelve el resultado sin HTTP ni credencial. De lo contrario consulta la tarea y descarga solo si SUCCEEDED con OBJ.
6. `result.ToImportRequest(unit, axis, heightMm, name, description)` prepara el contrato existente. El futuro coordinador 4B deberá llevarlo al hilo/contexto válido de Revit API; nunca usar Task.Run para ello ni bloquear la interfaz esperando generación.

Las solicitudes HTTP incluyen autenticación individual; no hay token global compartido con descargas. Las respuestas JSON tienen tope de 512 KiB y profundidad 32. IDs aceptados: UUID con formato D, como en la API documentada. Solo códigos de error documentados entran en el diario: no se conservan mensajes arbitrarios del proveedor ni excepciones de transporte con información potencialmente sensible.

## Costos, duplicados y modo real

`CreateReal()` falla por defecto. En el futuro necesita `enableRealNetwork: true`, almacén `Real`, credencial de entorno y `PaidGenerationApproval.ConfirmNewPaidGeneration(imageHash, options)` obtenido tras confirmar costo en la interfaz. La aprobación está ligada a la huella y se consume una sola vez. Ningún comando actual llama a esa fábrica.

La reserva se hace bajo bloqueo entre procesos antes de enviar; clientes simultáneos no crean dos tareas. Una desconexión, timeout, cancelación de creación, respuesta inválida o 5xx deja el intento incierto. Un rechazo HTTP 4xx queda identificado como rechazo y también impide repetir automáticamente la misma huella. No hay botón ni método que borre reservas o genere de nuevo para resolver errores. Para un nuevo intento legítimo se deberá reconciliar explícitamente en 4B. Recuperar un ID no prueba por sí solo que corresponda a una reserva incierta: se conserva también esa reserva hasta conciliación manual.

La protección se limita a esta instalación/directorio de datos. No pretende deduplicar frente a otras aplicaciones, equipos, usuarios o eliminación manual del diario. Conservar una copia del diario es importante; no lo borre para resolver problemas.

## Persistencia

Por defecto `%LOCALAPPDATA%\BIM Family AI\Meshy\Simulated\tasks.json` y carpeta paralela `Real`. El directorio es configurable para pruebas temporales. Se conserva huella, proveedor, ID, hash de imagen, perfil, fecha local de intento (`AttemptedAt`), creación remota cuando se conoce (`CreatedAt`), envío/estado/progreso, ruta/hash del OBJ, validación y errores clasificados. No se guarda ruta original de foto, base64, API key, Authorization ni URL de descarga.

`tasks.lock` se abre con FileShare.None; escritura a temporal propio con Flush y movimiento/reemplazo en el mismo volumen. Un cierre antes de guardar el ID deja la reserva previa, evitando repetir el POST. JSON corrupto/ilegible o con campos inválidos falla cerrado y conserva el archivo para reconciliación; no se sustituye silenciosamente por un diario vacío. Límite del diario: 10000 registros/4 MiB; requiere mantenimiento explícito futuro si se alcanza. Un error antes del reemplazo conserva la versión anterior.

## Descarga segura y caché

HTTPS, puerto estándar, host exacto `assets.meshy.ai`, sin userinfo/fragmento. Cliente propio sin Authorization/cookies, redirecciones desactivadas en transporte real y rechazo de respuestas 3xx. No se permite ampliar hosts desde datos devueltos por la API. Si Meshy cambia de CDN habrá que verificar oficialmente y actualizar la lista.

ResponseHeadersRead y bloques de 64 KiB, timeout local de 2 minutos, máximo 50 MiB tanto por Content-Length como por bytes recibidos, comprobación de longitud anunciada, temporal con extensión OBJ, `ObjInspection` y SHA256 antes de publicar. Archivo final con GUID y movimiento sin sobrescritura. Se eliminan parciales al fallar/cancelar, si los permisos del sistema lo permiten. Ninguna salida inválida se presenta como éxito.

Se mantienen 250000 vértices y 500000 caras, además de los límites estructurales actuales. El servicio no convierte RFA ni calcula dimensiones físicas. La inspección estructural no garantiza que Autodesk acepte toda malla: esa validación sigue en el importador existente.

## Validación ejecutada

Comandos reproducibles desde la raíz (sin clave):

```powershell
dotnet build .\BIMFamilyAI.sln -c Release -p:Platform=x64
dotnet run --project .\tests\BimFamilyAI.Core.Checks -c Release -- tests/fixtures
dotnet run --project .\tests\BimFamilyAI.Meshy.Checks -c Release -- tests/fixtures
```

Se usan ejecutables de comprobación, como en 3B, sin framework externo. Las fotografías de prueba se construyen localmente en C# y el OBJ es el fixture existente `cube_500mm.obj`. Todo SendAsync ejecutado en las pruebas usa handlers en memoria, con fallo inmediato ante solicitudes inesperadas. La guarda de fábrica real desactivada se ejercita antes de construir transporte. Para probar la política real (credencial/confirmación), una fábrica interna visible solo al ensamblado de comprobaciones inyecta también handlers simulados: ni siquiera una regresión en esas guardas puede enviar datos a producción desde estas pruebas. La clave del entorno se retira y se restaura al final sin mostrarla; solo las pruebas de política usan un marcador sintético que no es una credencial.

| Grupo | Cobertura |
|---|---|
| A | PNG real 32×32, Data URI, JSON exacto sin campos ignorados, Bearer simulado, 202/ID, diario, deduplicación |
| B | PENDING → IN_PROGRESS → SUCCEEDED, progreso, Retry-After segundos/fecha, backoff y límite GET |
| C | OBJ válido por transporte simulado, sin token en almacenamiento, inspección, contrato de importación |
| D | 400/401/402/403/404/429/500: clasificación, mensaje seguro y cero reintentos POST |
| E | Cancelar seguimiento, reiniciar cliente y recuperar ID con GET sin segundo POST |
| F | Vacío, HTML, stream roto, exceso por cabecera/stream, longitud incorrecta, HTTP/host/userinfo/puerto/redirección no confiables, expiración |
| G | Caché inspeccionada/hash tras reinicio, sin HTTP |
| H | Sin clave, sin secretos/base64/URLs en JSON/errores, guardas de modo real |
| Adicional | Creación incierta por desconexión/timeout/cancelación/JSON/ID inválido, carrera entre clientes, registro corrupto/almacenamiento, imágenes JPEG/PNG/WebP y límites |

- Compilación completa Release/x64 con SDK 10.0.404: **0 errores, 2 advertencias MSB3277** preexistentes, por Microsoft.VisualBasic y System.Drawing de Autodesk. Core/Meshy y comprobaciones no añaden advertencias. El primer intento del proyecto nuevo sin restore informó que faltaba project.assets.json; se restauró con el SDK y las compilaciones finales pasan. No se añadió ningún paquete externo.
- Core.Checks existente: **11 comprobaciones aprobadas**.
- Meshy.Checks: **125 comprobaciones aprobadas**. Incluyen aprobación ligada a imagen/perfil y consumible, protección aun después de rechazos HTTP, hash de caché alterada, ID conservado cuando falla su guardado y distinción entre fecha local/remota.
- **Cero solicitudes de generación reales, cero POST enviados a producción, cero créditos consumidos por esta etapa.** Los POST de pruebas solo alcanzan FakeHandler. Tampoco se ejecutaron GET/descargas reales de la API/CDN Meshy. Las consultas web se limitaron a documentación pública.
- Instalador existente ejecutado correctamente sin cambios, con Revit cerrado. Se comprobaron hashes SHA256 idénticos entre DLL Core/Revit x64 compiladas e instaladas. El adaptador Meshy no se copia al complemento porque aún no está conectado a Revit.
- Regresión real ejecutada en **Revit 2027.2**, desde `BFAI_Stage3A_Validation_20261115.rvt`: generador fijo creó `BIMFamilyAI_TestCube_500mm_5.rfa` (454656 bytes), con mensaje de cubo nativo 500×500×500 mm; paramétrico creó `BIMFamilyAI_ParametricBox_4.rfa` (458752 bytes), con dos tipos/material y validaciones superadas; importador creó `cube_500mm_1.rfa` (454656 bytes), fixture en milímetros, Z, sin altura objetivo, validado tras reabrir. Se conservaron nombres previos; no hubo sobrescritura.
- Después de los tres comandos se cerró el proyecto con Ctrl+W y volvió a Inicio sin aviso de guardar: no se modificó el proyecto de prueba. No se atribuyen a esta regresión mediciones manuales nuevas ni variantes distintas de las indicadas.
- Revisión final de cambios/espacios y referencias: sin modificación de algoritmos, scripts, comandos, fixtures OBJ ni ObjInspection; sin referencias Autodesk o paquetes externos en Meshy/Core. No hay credenciales reales en los archivos añadidos. Solo se actualizó trabajo local, sin commits ni cambios remotos.

## Archivos de esta etapa

Nuevos: `src/BimFamilyAI.Core/Generation.cs`; `src/BimFamilyAI.Meshy/{BimFamilyAI.Meshy.csproj,MeshyOptions.cs,LocalImage.cs,JsonTaskStore.cs,MeshyClient.cs,MeshyDownload.cs}`; `tests/BimFamilyAI.Meshy.Checks/{BimFamilyAI.Meshy.Checks.csproj,Program.cs,TestSupport.cs}`; este informe.

Modificados: `BIMFamilyAI.sln`, `README.md`, `docs/architecture.md`. No hay cambios de geometría, fixtures OBJ, comandos o scripts. No se crean commits, PR ni escrituras en el remoto.

## Pendientes y acciones manuales para 4B

No necesita proporcionar una clave ni activar Meshy para repetir estas pruebas. Para 4B habrá que autorizar por separado la prueba pagada, comprobar de nuevo contrato/precios/retención, configurar MESHY_API_KEY fuera de Git y construir la confirmación de costo por intento. También implementar selector/seguimiento asíncrono, recuperación visible de IDs/intentos inciertos, confirmación de dimensión física y entrega mediante ExternalEvent u otro contexto válido Revit.

No se han probado generaciones reales, credenciales reales, red/CDN reales, calidad visual de mallas IA, texturas, otros modelos ni el flujo UI 4B. La futura aprobación de interfaz debe llamar a la aprobación de código únicamente tras consentimiento del usuario. Esta etapa no avanza a 4B.
