# Etapa 4B — Fotografía, Meshy y Revit

**Etapa 4B completada en su alcance simulado; generación real fuera de esta validación.** Ninguna prueba de desarrollo debe crear una tarea pagada. El modo inicial es simulado y su transporte no accede a producción.

## Arquitectura

Se reutilizan Core, el cliente Meshy de 4A, su diario y el importador de 3B. No se añaden paquetes, proveedores, backend, base de datos ni algoritmos de conversión.

- `CreateFamilyFromPhotoCommand`: cuarto comando, abre una sola ventana modeless, asociada mediante el HWND de Revit. GUID nuevo: `D0A1F906-F8C2-4DDA-947A-F1E53FBB61C7`. Los tres GUID anteriores se conservan.
- `PhotoFamilyWindow`: selección/inspección y vista previa, perfil T2, modo simulado/real, altura y orientación, historial, progreso, cancelación local, rutas, carpetas y apertura de RFA. Dos operaciones separadas: generar/recuperar OBJ y crear RFA.
- `PhotoGenerationSession` en Meshy: coordinación asíncrona independiente de Autodesk; una operación a la vez, consulta de huella antes de crear, autorización solo para una creación real nueva, seguimiento y reutilización. La preparación/inspección puede ejecutarse en un worker; nunca llama a Revit API.
- `MeshySimulation`: handlers en memoria con fixture OBJ embebido desde `tests/fixtures/cube_500mm.obj`. Escenarios normales, seguimiento lento y errores. Sus contadores describen solicitudes simuladas, no tráfico de producción. No interpreta la fotografía ni produce un modelo IA.
- `PhotoFamilyExternalEvent`: creado en contexto válido del comando y conservado durante la ventana. Encola una solicitud inmutable; Raise solicita atención de Revit; Execute recibe UIApplication y llama al flujo existente. No hay llamadas de documentos/transacciones desde continuaciones asíncronas ni Task.Run.
- `FamilyFileWorkflow.CreateFamily(Application, ...)`: refactor mínimo para devolver estado, ruta y errores. El wrapper anterior conserva los diálogos/comportamiento de los tres comandos. Geometría, escala, límites, guardado y reapertura siguen en el motor existente.

El resultado Meshy no tiene unidades físicas confiables. `PhotoFamilyRequest.Create` exige altura finita positiva y un eje explícito. Usa una base numérica de unidades cuya influencia se cancela al escalar por altura objetivo; el metadato indica **unidades no declaradas, escala por altura confirmada**. El importador manual conserva sus unidades seleccionadas. No se cambió su algoritmo geométrico.

Los nombres de prueba reciben `SIM_` y su descripción identifica el OBJ fijo de simulación. El resultado real se describe como geometría IA con dimensiones informativas, sin afirmar parametrización nativa ni propiedades técnicas no verificadas.

## Ventana y ciclo de vida

La ventana no modal deja a Revit disponible durante HTTP. Los controles evitan doble activación y la sesión añade una guarda propia. La importación síncrona sí puede ocupar Revit mientras ejecuta su API oficial. Si está en otro comando, una solicitud ExternalEvent puede esperar a que finalice.

La ventana mantiene el evento y permite abrir el RFA con otra solicitud oficial. Al cerrar cancela el seguimiento local, conserva diario/OBJ y cancela una importación aún encolada; si la importación nativa ya está ejecutándose, solicita esperar. La baja del evento ApplicationClosing y la liberación de ExternalEvent se realizan en contexto API mediante una solicitud de limpieza. Durante el cierre de Revit, ApplicationClosing cierra la ventana y libera el evento sin crear documentos ni nuevas tareas. Si Revit ya rechaza la solicitud de limpieza, el manejador de cierre conserva únicamente la responsabilidad de liberarla al terminar Revit, sin referencias a la ventana.

La vista previa usa los codecs WPF/Windows y un tamaño de decodificación reducido. Un WebP sin codec disponible mantiene su validación estructural y muestra un aviso de vista previa no disponible. No se instala ningún codec ni editor de fotos. Se conservan límites y alcance de inspección de 4A; no es una decodificación exhaustiva de todos los píxeles comprimidos.

## Contrato oficial y costo verificados

Verificados antes de programar el 16/11/2026:

- [Image to 3D](https://docs.meshy.ai/en/api/image-to-3d): T2/Smart Topology, sin texturas, 4000 caras por defecto (100–15000), salida OBJ. No se envían campos incompatibles/ignorados. No se usan modelos obsoletos.
- [Precios API](https://docs.meshy.ai/en/api/pricing): T2 sin texturas indica **5 créditos API por tarea**. La interfaz muestra la fecha de consulta y que la tarifa puede cambiar; no presenta el conteo de caras como descuento. Los créditos API son distintos de los de la aplicación web.
- [Autenticación](https://docs.meshy.ai/en/api/authentication): clave propia de entorno, Bearer por solicitud. No se usa ninguna antigua clave compartida. El programa nunca solicita la clave en un formulario, chat o log.
- [Ciclo de tareas](https://docs.meshy.ai/en/api/task-lifecycle): creación asíncrona con ID, seguimiento por GET, Retry-After o 5 s, reintentos GET limitados y cancelación únicamente local. Un timeout no demuestra que no exista una tarea.
- [Retención](https://docs.meshy.ai/en/api/asset-retention): hasta 3 días salvo Enterprise; volver a consultar puede devolver el mismo enlace y expiración. La copia propia sigue siendo necesaria. No se regenera por un enlace vencido.
- [External Events, Autodesk Revit 2027](https://help.autodesk.com/cloudhelp/2027/ENU/Revit-API/files/Revit_API_Developers_Guide/Advanced_Topics/Revit_API_Revit_API_Developers_Guide_Advanced_Topics_External_Events_html.html), contrastado con RevitAPIUI.xml instalado: Create, Raise, Execute, estados de Raise, ApplicationClosing no cancelable y disposición de recursos.

## Autorización y recuperación

Abrir Revit/ventana, cambiar modo, seleccionar una foto y leer historial no ejecuta POST. El modo real necesita MESHY_API_KEY y, solo si corresponde crear, muestra un diálogo específico con proveedor, perfil, fotografía/hash, costo estimado y advertencia de calidad/cobro. Cancelar tiene foco y es la acción predeterminada; autorizar no es predeterminado. La aprobación de 4A queda ligada a foto/perfil y se consume una sola vez. Un cambio de archivo/configuración invalida esa aprobación para una creación distinta.

El historial usa las mismas carpetas Simulated/Real de 4A, sin mezclar resultados. Un ID puede consultarse, seguirse y descargarse sin foto ni otro POST. Un OBJ validado se reutiliza sin HTTP. Un fallo de importación permite repetir solo la creación del RFA.

Una creación incierta mantiene su reserva. El usuario debe comprobar en Meshy la foto/perfil/momento del envío y recuperar un ID. **Conciliar** exige confirmación de esa evidencia; el programa consulta el ID para comprobar existencia/acceso, pero no afirma comprobar automáticamente la asociación visual. La conciliación conserva la huella y una auditoría tipada/fechada. No elimina la reserva para generar de nuevo ni asocia un ID que ya corresponda a otra foto.

Un rechazo confirmado puede reintentarse únicamente después de confirmar la corrección del motivo, con una nueva autorización pagada cuando sea real. El reemplazo de la reserva se protege bajo bloqueo entre procesos, conserva error/auditoría anterior y no se aplica a envíos inciertos. Solo rechazos HTTP documentados habilitan esa vía; 408, 409 y respuestas desconocidas conservan la incertidumbre. `RetryNotBefore` guarda Retry-After y bloquea reintentos anticipados incluso tras reiniciar, antes de pedir otra autorización. No existen reintentos automáticos de POST. Los registros 4A sin auditoría siguen leyéndose con lista vacía. Se conservan topes del diario y de OBJ.

## Compilación, instalación y comprobaciones

```powershell
dotnet build .\BIMFamilyAI.sln -c Release -p:Platform=x64
dotnet run --project .\tests\BimFamilyAI.Core.Checks -c Release -- tests/fixtures
dotnet run --project .\tests\BimFamilyAI.Meshy.Checks -c Release -- tests/fixtures --write-preview
```

La última opción genera `artifacts/stage4b-photo.png`, una imagen sintética local para probar selección/vista previa. No contiene una foto privada ni se sube a ningún sitio. Los handlers de todas las comprobaciones son simulados, incluidas las pruebas de política real. Una regresión de sus guardas tampoco podría enviar POST a producción desde esos tests.

La instalación copia Core/Revit/Meshy y Revit.deps.json; el OBJ de prueba viaja como recurso embebido. No copia DLL Autodesk. Con Revit cerrado, ejecutar `scripts/Install-Addin.ps1`; con `-Uninstall` elimina exclusivamente el manifiesto y archivos propios conocidos y conserva familias, historial y archivos ajenos.

Resultados de compilación final: solución Release/x64 con SDK 10.0.404, **0 errores y 2 MSB3277 preexistentes** de Autodesk (Microsoft.VisualBasic/System.Drawing). Core: **11 comprobaciones aprobadas**. Meshy/sesión: **187 comprobaciones aprobadas**, incluidas las 125 de 4A y 62 de 4B. Instalación repetida, cuatro GUID únicos, desinstalación segura con archivo centinela ajeno a la lista y RFA conservado por hash, e instalación final: aprobadas. Los hashes de las tres DLL compiladas/desplegadas coinciden.

Pruebas reales de interfaz/API local realizadas en Revit 2027.2 con transporte Meshy simulado:

| Caso | Ejecución y evidencia |
|---|---|
| A | PNG sintético 64×64 seleccionado con vista previa; doble clic generó un solo POST simulado. Secuencia/progreso y OBJ válido. ExternalEvent creó `SIM_Stage4B_750.rfa` (454656 bytes), lo reabrió/validó y el botón Abrir RFA lo abrió en Revit. Vista 3D inspeccionada y Tipos de familia mostró Altura/Ancho/Profundidad=750 mm. |
| C/D | Tras cerrar/reabrir la ventana se recuperó el OBJ por historial sin fotografía y con POST=0, GET=0, descargas=0. Altura vacía fue rechazada antes de encolar. Altura 1000000 mm falló en el límite geométrico del importador, conservando OBJ/tarea; corregir a 500 mm creó `SIM_Familia_Foto.rfa` (454656 bytes) sin HTTP. |
| E | Doble clic inicial: POST=1, cuatro consultas simuladas y una descarga; no hubo segunda tarea. Guarda de sesión y reserva entre clientes comprobadas además por tests. |
| H | Se revisó el diario de la prueba UI: sin Data URI/base64, URLs firmadas, claves ni Authorization. Las simulaciones no requieren clave; las pruebas de política usan solo marcadores sintéticos y handlers falsos. |
| I | Desde el proyecto de prueba: `BIMFamilyAI_TestCube_500mm_6.rfa` (454656 bytes), `BIMFamilyAI_ParametricBox_5.rfa` (458752 bytes) y `cube_500mm_2.rfa` (454656 bytes; mm, Z). Los tres comandos informaron éxito; paramétrico/OBJ validaron reapertura. |
| J | `BFAI_Stage3A_Validation_20261115.rvt` se cerró con Ctrl+W directamente a Inicio, sin pedir guardar. Abrir/inspeccionar el RFA propio sí creó cambios de vista que se descartaron explícitamente al cerrar esa familia; eso no modificó el proyecto. No se cargaron instancias. |

Cierre de la ejecución interrumpida (16/11/2026): se retomó `main` limpio, commit `2403f1a` (Etapa 4B-1), coincidente con `origin/main` local, sin descartar cambios ni publicar. La cancelación lenta anterior conservó el ID `9a46c29c-ba72-44a9-8b98-331b6374fa8d`. En esta ejecución, tras reiniciar Revit/ventana, se seleccionó ese Pending y se reanudó en escenario Normal: mismo ID, SUCCEEDED, OBJ validado, POST=0, GET=4, descargas=1. El hash del archivo coincide con el fixture `cube_500mm.obj`; no es una reconstrucción IA. Cambiar a Real mostró el aviso de MESHY_API_KEY ausente y el historial real vacío, sin pulsar Generar ni consultar producción. El test de política real con handlers falsos comprobó además cero solicitudes y cero reservas sin credencial. Los siete errores API requeridos (401/402/429/timeout/tarea fallida/URL vencida/OBJ inválido) ya se ejercitaron en el simulador de ejecución y en la sesión, además de los tests de transporte 4A. También se verificaron conciliación con evidencia, reintento corregido con aprobación nueva, cancelación de autorización sin reserva/POST y cambio de foto tras confirmar que invalida la aprobación. No se atribuye a esos tests ejecución de Autodesk fuera de Revit.

**Cero POST de generación enviados a producción, cero generaciones pagadas y cero créditos consumidos por esta implementación.** Tampoco se realizaron GET/descargas de producción Meshy. Solo se consultó documentación pública. No se ha probado que funcione la API/CDN de producción con una cuenta real.

## Inventario de archivos

Nuevos: `src/BimFamilyAI.Meshy/MeshyRecovery.cs`, `MeshySimulation.cs`, `PhotoGenerationSession.cs`; `src/BimFamilyAI.Revit/CreateFamilyFromPhotoCommand.cs`, `PhotoFamilyExternalEvent.cs`, `PhotoFamilyWindow.cs`; `tests/BimFamilyAI.Meshy.Checks/Stage4bChecks.cs`; este informe.

Modificados: `README.md`, `docs/architecture.md`, `scripts/Install-Addin.ps1`; Core `Generation.cs` y únicamente el contrato de `ObjInspection.cs`; Meshy `BimFamilyAI.Meshy.csproj`, `MeshyClient.cs`, `MeshyOptions.cs`, `JsonTaskStore.cs`; Revit `BimFamilyAI.Revit.csproj`, `FamilyFileWorkflow.cs` y metadato de unidades en `ObjFamilyImporter.cs`; `tests/BimFamilyAI.Meshy.Checks/Program.cs`.

Los tres archivos de comandos previos, ParametricBoxBuilder, algoritmos de inspección/escala/geometría, fixtures OBJ, solución y configuración SDK permanecen sin cambios. El refactor del flujo y el metadato de unidades son los cambios mínimos necesarios para ExternalEvent y para no atribuir unidades físicas a una malla IA.

## Guía sencilla: prueba gratuita

1. Cierre Revit, instale con el script y vuelva a abrirlo. Si Revit muestra una advertencia del complemento local sin firma, revísela y resuélvala personalmente.
2. Abra un proyecto de prueba guardado. Entre en **Complementos → Herramientas externas → BIM Family AI — Crear familia desde fotografía**.
3. Mantenga **Prueba (simulado, 0 créditos)** y escenario **Normal**.
4. Seleccione su JPG/PNG/WebP o `artifacts/stage4b-photo.png`. Revise nombre/vista previa.
5. Pulse **Generar modelo 3D**. Verá ID y progreso y obtendrá el cubo OBJ fijo de prueba, no una reconstrucción de la foto.
6. Escriba nombre, altura conocida en mm (por ejemplo 750) y eje Z. Pulse **Crear familia RFA**. El ejemplo debe resultar en un cubo de 750 mm, centrado y apoyado en Z=0.
7. Pulse **Abrir RFA en Revit** y abra su vista 3D. Revise dimensiones informativas en Tipos de familia. No son restricciones paramétricas.
8. Puede repetir Crear RFA sobre el mismo OBJ sin generar. Los archivos nuevos reciben sufijo; no sobrescriben otros RFA.
9. Para interrumpir una espera, pulse Detener seguimiento o cierre la ventana. Reabra, seleccione la tarea del historial y pulse Continuar / descargar OBJ. No necesita volver a pagar.

Para probar errores sin red, use los escenarios de prueba. El perfil/imagen repetidos reutilizan una tarea previa por diseño; cambie explícitamente el número de caras para un caso simulado nuevo si necesita ensayar otro escenario. No use el modo real para esas pruebas.

## Guía sencilla: primera generación REAL (solo el usuario)

1. Cree su cuenta en Meshy y revise/habilite su acceso API de pago. Compruebe su saldo de **créditos API**, separado del saldo web. Esta aplicación no compra créditos ni gestiona suscripciones.
2. Cree una clave propia en el Developer Platform de Meshy. No la comparta en ChatGPT/GitHub/capturas. Puede limitar su gasto desde Meshy.
3. En Windows, busque **Editar variables de entorno de esta cuenta**. En **Variables de usuario**, cree `MESHY_API_KEY` y pegue la clave en Valor. Confirme y cierre los diálogos. No la guarde en archivos del repositorio.
4. Cierre todas las instancias de Revit y vuelva a abrirlo para heredar la variable. Si Windows conserva un entorno anterior, cierre sesión e ingrese de nuevo. La ventana mostrará instrucciones si falta la variable.
5. Abra el cuarto comando y cambie a **Real (Meshy API)**. Seleccione una fotografía que pueda enviar a Meshy. Confirme perfil, 4000 caras, sin texturas y OBJ.
6. Consulte la [tarifa actual](https://docs.meshy.ai/en/api/pricing). Pulse Generar modelo 3D y lea el diálogo completo. Solo usted debe pulsar **Autorizar UNA generación** si acepta el envío y costo; Cancelar no crea tareas.
7. Conserve el ID mostrado. Espere progreso/descarga. Detener el seguimiento o cerrar no garantiza cancelar el trabajo ni su cobro. Puede reanudar por historial/ID sin nuevo POST.
8. Si ocurre timeout de creación, no borre el diario ni repita. Compruebe su actividad en Meshy, recupere el ID si existe y use Conciliar con evidencia de asociación. Si no puede saber si fue aceptada, mantenga la reserva y consulte al proveedor.
9. Cuando el OBJ esté validado y descargado, confirme una **altura física medida** y el eje vertical. Sin altura, conserve el OBJ y no cree una familia presentada como dimensionalmente verificada.
10. Pulse Crear RFA, luego Abrir RFA. Inspeccione vista 3D, categoría Modelo genérico y dimensiones (altura y proporciones), centrado y base. La calidad/fidelidad de una reconstrucción IA debe evaluarse; no se garantizan propiedades técnicas ni materiales perfectos.
11. Si falla solo Revit, corrija altura/eje/nombre/plantilla y repita Crear RFA con el mismo OBJ. No pulse una generación nueva para resolver un error de conversión.

## Límites y pendientes

La API/CDN y credenciales reales, el costo aplicado a una cuenta real, la calidad de reconstrucción, texturas y modelos diferentes permanecen sin probar. El perfil y precio deben revisarse si Meshy cambia el contrato. La vista previa WebP depende del codec local, pero el cliente puede preparar su Data URI sin él. El JSON corrupto se conserva y bloquea operaciones hasta reconciliarlo; no se borra automáticamente. La deduplicación abarca el mismo directorio de datos, no otros equipos o borrado manual.

El importador conserva límites de 3B; no se elevan para acomodar mallas incompatibles. La importación oficial es síncrona y no tiene cancelación a mitad de la llamada. Los resultados son familias geométricas; para geometría que responda a parámetros nativos use el comando de 3A.

No se realizan commits, publicaciones ni cambios remotos. No se avanza a etapa 5.

## Cierre: revisión técnica y verificaciones adicionales

Se demostró y corrigió únicamente un defecto: el botón de reintento de un rechazo podía usar la foto/perfil actuales sin comprobar su asociación al registro seleccionado. Ahora la ventana captura la huella seleccionada y la sesión exige que coincida con la foto y opciones antes de consultar/reutilizar, pedir autorización o crear una reserva. Dos comprobaciones nuevas demuestran rechazo de la discrepancia y ausencia de POST/reserva adicional. Se reforzó también la comprobación de credencial ausente para verificar historial vacío.

Cambios de esta ejecución: `PhotoGenerationSession.cs`, `PhotoFamilyWindow.cs`, `Stage4bChecks.cs` y este informe. README y arquitectura se revisaron y no necesitaron cambios. No se modificaron los algoritmos geométricos, comandos anteriores, instalador ni GUID.

La revisión técnica confirma que las continuaciones de ventana vuelven al dispatcher WPF; el callback de aprobación se entrega explícitamente con Dispatcher. Los Task.Run se limitan a imagen y contratos Core. La sesión tiene exclusión Interlocked y la ventana bloquea acciones mientras hay una operación. El cierre cancela el seguimiento y espera la operación antes de disponer el cliente; los callbacks de progreso comprueban closed/version. La limpieza de ExternalEvent y su suscripción ocurre en Execute/ApplicationClosing. Un fallo de importación conserva ID/OBJ.

ExternalEvent se probó con el comando Muro activo, sin dibujar: Crear RFA quedó encolado y los controles se deshabilitaron; Escape terminó Muro y ejecutó una sola importación, guardando/reabriendo `SIM_Familia_Foto_1.rfa` (750 mm, 454656 bytes), sin POST/GET/descargas. Se repitió la cola, se cerró la ventana antes de terminar Muro y después se pulsó Escape: no se creó otro RFA. Los dos RFA anteriores permanecieron, sin documento de familia activado ni error visible.

La regresión paramétrica anterior ejecutó CreateAndValidate y ValidateSaved dentro de Revit: cambio de Ancho/Profundidad/Altura a 1400/800/950, comprobación de caja y asociación de Material/altura tras reapertura, con rollback de las comprobaciones. El importador validó categoría Modelo genérico, escala, centrado, base y geometría antes y después de guardar. Esto complementa la inspección visual del cubo de 750 mm ya documentada; no se presenta como una nueva inspección visual manual de materiales.

Registros locales de esta ejecución: `artifacts/stage4b-closure-build.log`, `stage4b-closure-core.log`, `stage4b-closure-checks.log`, `stage4b-closure-install.log` (ignorados por Git). Instalación actualizada con Revit cerrado: cuatro comandos/GUID únicos y únicamente Core.dll, Meshy.dll, Revit.dll y Revit.deps.json; los tres hashes de DLL desplegadas coinciden con la compilación.

La revisión instalada también se probó cerrando la ventana durante seguimiento lento (perfil 4002): POST simulado=1, ID `b8487e43-0dd1-491e-bab9-b0f49332e58c`, Pending/0 y sin descarga. La ventana volvió a abrir correctamente y mostró ese ID en historial; el seguimiento anterior había terminado localmente. No se detectaron errores visibles ni duplicados. Estas observaciones y la revisión del código verifican el ciclo funcional; no se hizo un perfilado de memoria ni una prueba de estrés de fugas de recursos.

Reanudación de la última revisión instalada: `b8487e43-0dd1-491e-bab9-b0f49332e58c` alcanzó SUCCEEDED y OBJ válido al pulsar Continuar en Normal, con POST=0, GET=4 y descarga=1. Después se cerró Revit normalmente mediante su X, manteniendo abierta la ventana del complemento: ambas ventanas desaparecieron, el proceso terminó sin forzarlo y sin diálogo de error/guardado. `journal.0057.txt` finalizó con ExitNativeInstance y finished recording journal file; no apareció una excepción del complemento al cerrar. Esta prueba cubre ApplicationClosing con la ventana abierta y en reposo; el cierre durante seguimiento se probó en la ventana, como se detalla arriba.

**Resultado de cierre: completada la etapa 4B simulada.** Compilación Release/x64: 0 errores, 2 MSB3277 preexistentes sin ocultar. Core=11 y Meshy=187 aprobadas. `git diff --check` sin errores de espacios; Git avisa de conversión LF/CRLF en los archivos editados, sin cambios de contenido derivados. Se revisó el diff completo: solo los cuatro archivos enumerados arriba. No se crearon commits ni publicaciones. Se conservaron diarios, OBJ y RFA. Cero solicitudes Meshy de producción y cero créditos consumidos.

No queda una acción manual necesaria para cerrar la simulación. El complemento corregido está instalado; Revit se dejó cerrado tras la prueba de cierre. Para repetir, abra Revit y use el cuarto comando en Prueba (simulado, 0 créditos). La integración queda preparada para una futura prueba real autorizada por el usuario; credenciales reales, conectividad API/CDN, facturación y calidad IA no se han validado y no forman parte de este cierre. No se inició etapa 5.
