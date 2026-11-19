# Arquitectura de BIM Family AI

## Componentes actuales

- **BimFamilyAI.Core**, net10.0: contratos de generación independientes del proveedor, estados/progreso y resultado OBJ; ObjImportRequest y ObjInspection existentes. Sin Autodesk, HTTP ni credenciales.
- **Diagnóstico 5D-A en Core**: `ObjStructure` valida primero con `ObjInspection` y después lee el OBJ para contar declaraciones `o`/`g`/`usemtl`/`mtllib`, distribuciones de caras y componentes unidos por índices de posición. Devuelve cajas/centros y advertencias sobre duplicados y solapamientos de cajas. Solo lectura; no exporta ni cambia mallas, Meshy, importación o RFA. `Core.Checks --analyze archivo.obj` emite JSON. [Evidencia y límites](stage5d-a-analysis.md).
- **Extracción 5D-B1 en Core**: `ObjComponentExtractor` reutiliza la asignación de caras de `ObjStructure`, remapea índices de vértices/UV/normales por componente, conserva sus coordenadas originales y comprueba la correspondencia exacta antes de publicar una carpeta nueva con OBJ, MTL local seguro cuando es necesario y manifiesto JSON. No aplica escala, eje ni centrado; el manifiesto aporta caja global y números originales de caras/vértices para un único posicionamiento global futuro en 5D-B2. [Validación](stage5d-b1-validation.md).
- **Ensamblaje 5D-B2**: `ObjComponentExtractor.Verify` vuelve a contrastar origen, piezas, hashes y correspondencia de caras antes de importar. `ObjAssemblyTransform` calcula una escala, rotación y traslación desde la caja global. `ObjAssemblyImporter` aplica esa transformación a cada importación embebida en una única familia y valida cajas individuales, identidad, dimensiones y RGB tras guardar/reabrir. La modalidad OBJ completo sigue en `ObjFamilyImporter`. [Validación](stage5d-b2-validation.md).
- **Etiquetas 5D-C1 en Revit**: `ComponentLabels` lee la identidad inmutable de cada `ImportInstance` 5D-B2 y guarda el nombre manual en un esquema Extensible Storage complementario. La selección, lectura y transacción se procesan en el `ExternalEvent` existente; la ventana no modal solo muestra resultados y solicita operaciones. No cambia geometría, materiales ni el esquema de identidad original. [Validación](stage5d-c1-validation.md).
- **Regiones de caras 5D-C2A**: `ObjComponentExtractor.DeriveRegions` asocia números de cara del OBJ original a regiones RGB dentro de una pieza 5D-B1 y publica OBJ/MTL derivados con manifiesto y hashes separados. `VerifyRegions` compara vértices, UV, normales, esquinas y orden de cada cara con la pieza original. `ObjAssemblyImporter` usa la copia derivada para la pieza afectada, conserva la transformación global y añade un esquema de procedencia de regiones sin cambiar el esquema de identidad 5D-B2. La ruta es explícita mediante `ObjAssemblyRequest.RegionDirectory`; aún no se expone un selector gráfico de caras en WPF. [Validación](stage5d-c2a-validation.md).
- **BimFamilyAI.Meshy**, net10.0: adaptador Image to 3D con entrada local, perfil económico T2, cliente HTTP asíncrono, seguimiento, descarga y diario JSON. Referencia solo Core. No añade paquetes externos.
- **Editor de regiones 5D-C2B**. Core lee la extracción 5D-B1 verificada y asigna a cada triángulo de vista previa el ID de componente y número de cara original. WPF dibuja con `Viewport3D`. El editor usa un rayo ortográfico y adyacencia por índices de arista, organiza las selecciones como `ObjRegionPlan` y reutiliza la validación y derivación 5D-C2A. `PhotoFamilyWindow` prepara las carpetas locales, luego el `ExternalEvent` crea y reabre el RFA. Para un RFA activo del mismo origen se trasladan las etiquetas que coinciden por identidad completa. [Validación 5D-C2B](stage5d-c2b-validation.md).
- **BimFamilyAI.Revit**, net10.0-windows x64: cuatro comandos, geometría nativa, familia paramétrica, importación OBJ y ventana modeless de fotografías. Único proyecto que conoce Autodesk; referencia Core y Meshy. El instalador despliega las tres DLL propias.
- **Comprobaciones**: ejecutables Core.Checks y Meshy.Checks independientes de Autodesk; fixtures propios y transporte HTTP en memoria.

```text
Foto local -> LocalImage -> MeshyClient -> ID / estados -> OBJ local validado
                              |                              |
                      JsonTaskStore                    GeneratedObjResult
                              |                              |
                  LocalAppData/Simulated o Real       ObjImportRequest
                                                             |
                                         PhotoFamilyWindow / ExternalEvent
                                                             |
                                              ObjFamilyImporter (Revit)
                                                             |
                                               FamilyFileWorkflow -> RFA
```

## Integración Meshy (4A)

LocalImage inspecciona contenedor, formato y dimensiones de JPG/JPEG, PNG y WebP y conserva bytes/hash para preparar Data URI en memoria. No sube fotos a un servidor público ni persiste base64. Es inspección de metadatos/estructura, no decodificación completa de píxeles. MeshyOptions expone únicamente el perfil compatible implementado: Smart Topology/T2, 4000 caras configurables entre 100 y 15000, sin texturas y OBJ. Más modelos o proveedores exigirán adaptadores/perfiles explícitos: no se añaden ahora interfaces sin uso.

CreateAsync valida, consulta la huella imagen+perfil y guarda una reserva antes del único POST. Guarda el ID inmediatamente al recibirlo. Intentos inciertos o rechazados impiden repetición automática. En 4B ReconcileAsync permite asociar explícitamente un ID comprobado por el usuario a una reserva incierta, tras GET y con auditoría tipada; RetryRejectedAsync exige corrección confirmada y nueva aprobación pagada para un rechazo confirmado. No permite repetir intentos inciertos. RecoverAsync reanuda por ID sin fotografía ni POST. QueryAsync/TrackAsync interpretan estados y progreso; esperan Retry-After o al menos 5 segundos. Solo GET tiene reintentos transitorios limitados. La cancelación es local: la tarea del proveedor puede continuar y el ID se conserva.

GetResultAsync reutiliza primero la copia propia validada por ObjInspection y SHA256. Si falta, obtiene un OBJ de una tarea exitosa mediante un cliente sin token. HTTPS y host exacto assets.meshy.ai, sin redirecciones; streams limitados a 50 MiB, temporales propios y publicación sin sobrescritura. Las URLs firmadas solo viven en memoria; la expiración permite una consulta adicional por ID, nunca una generación sustituta. Véanse [documentación y diferencias oficiales](stage4a-validation.md).

JsonTaskStore conserva exclusivamente metadatos seguros en LocalAppData/BIM Family AI/Meshy, separado por modo. Bloqueo de archivo entre procesos y reemplazo atómico en el mismo volumen. Un JSON corrupto se conserva y bloquea nuevas operaciones hasta reconciliarlo; no se descartan reservas. No almacena claves, fotos/base64 ni enlaces de descarga.

CreateSimulated recibe manejadores simulados y no construye transporte de red. MeshySimulation proporciona esos handlers en ejecución, con OBJ fixture embebido y escenarios de prueba. CreateReal exige habilitación expresa; una creación nueva necesita además MESHY_API_KEY y una aprobación consumible ligada a imagen/configuración. La ventana 4B muestra costo y obtiene confirmación antes de construir esa aprobación; Cancelar es predeterminado. No se implementa cancelación remota.

GeneratedObjResult.ToImportRequest conserva el contrato general. PhotoFamilyRequest añade la validación de altura obligatoria para fotografía, etiqueta simulaciones y marca unidades de origen no declaradas. La altura confirmada calibra escala uniforme; no se deduce tamaño físico de la imagen. La importación sigue usando el motor 3B, sin un segundo conversor.

## Coordinación e interfaz (4B)

PhotoGenerationSession, independiente de Autodesk, coordina validación, detección de tareas, confirmación de costo, seguimiento y caché, con una operación a la vez. PhotoFamilyWindow selecciona fotografía, muestra vista previa/perfil/estado/historial y separa generar modelo de crear RFA. La simulación inicial funciona sin clave ni red. Puede detenerse/cerrarse sin perder ID/OBJ y reanudarse por historial o ID manual. Los resultados simulados se identifican tanto en ventana como en nombre y descripción del RFA.

CreateFamilyFromPhotoCommand crea PhotoFamilyExternalEvent en contexto API. La ventana conserva su instancia y envía una solicitud completa e inmutable. Raise es el único acceso de señalización desde la ventana; IExternalEventHandler.Execute recibe UIApplication y realiza la importación o apertura del RFA. FamilyFileWorkflow.CreateFamily acepta Application y devuelve estado/ruta/errores; el wrapper existente mantiene los tres comandos previos. No se usa Task.Run para Autodesk. El cierre cancela esperas y solicitudes aún encoladas, y libera suscripciones/evento desde contexto válido; ApplicationClosing cubre el cierre de Revit. Ver [4B](stage4b-validation.md) para ciclo de vida y resultados observados.

## Funciones Revit conservadas

GenerateTestFamilyCommand crea una extrusión sólida de 500 mm. GenerateParametricFamilyCommand usa ParametricBoxBuilder: una extrusión, planos/cotas y fórmulas de semidimensiones para centrado, parámetros de tipo Ancho/Profundidad/Altura/Material, dos tipos y validación de flexión/material y reapertura. Ver [etapa 3A](stage3a-validation.md).

ImportObjFamilyCommand recoge unidades, eje y altura/nombre/descripción. ObjFamilyImporter usa importación nativa OBJ, escala uniforme con UnitUtils y CustomScale explícito, libera la fijación del elemento, rota, centra X/Y y apoya Z=0. Comprueba dimensiones, geometría y parámetros informativos. No reconstruye ni parametriza la malla. Ver [etapa 3B](stage3b-validation.md) para las convenciones de escala comprobadas y cobertura real.

FamilyFileWorkflow selecciona plantilla de Modelo genérico, crea documento independiente, ejecuta el generador/importador y guarda en temporal. Reabre familias paramétricas/OBJ para verificar persistencia y publica con sufijo sin sobrescribir en Documentos/BIM Family AI/Generated. Cierra documentos y comunica errores de limpieza. Es compartido por los cuatro comandos; el refactor 4B cambia únicamente contexto de entrada y resultado estructurado.

Para 5D-B2, `PhotoFamilyWindow` ofrece explícitamente modo simple o por componentes, admite OBJ local existente y prepara la extracción fuera del hilo API. `PhotoFamilyExternalEvent` lleva la solicitud inmutable a Revit. `FamilyFileWorkflow` crea un documento de familia por ensamblaje, delega en `ObjAssemblyImporter` dentro de transacción y publica únicamente después de comprobar la reapertura. Cada `ImportInstance` conserva una identidad de componente y hashes en Extensible Storage; los comentarios de instancia se rellenan si son editables. El RGB de una pieza se aplica solo a un material propio de su importación derivada. No se asignan nombres semánticos, sólidos nativos ni overrides de vista.

Límites OBJ conservados: 50 MiB, 250000 vértices, 500000 caras, 1024 índices/cara, 65536 caracteres/línea y coordenadas finitas hasta 1e8. El importador limita diagonal física a 0.1 mm–100 m y verifica centrado/base/dimensiones. ObjInspection es validación estructural; Autodesk puede rechazar una malla que la supere.

## Compilación, instalación y límites

Windows/Revit 2027.2 instalados; SDK 10.0.404 seleccionado por global.json desde 10.0.100 con latestFeature. Directory.Build.targets resuelve RevitInstallDir/REVIT_2027_DIR; referencias Autodesk con Private=false y error claro si faltan. El script instala solamente las DLL propias de Core/Revit/Meshy, deps.json y un manifiesto con cuatro GUID fijos. No se distribuyen DLL ni plantillas Autodesk.

La solución conserva configuraciones x64. Las dos advertencias MSB3277 de Microsoft.VisualBasic/System.Drawing provienen de dependencias de Autodesk y ya existían; no se ocultan. No hay backend, base de datos, cloud propio, OpenAI, Blender ni editor de imágenes. WPF incorpora la ventana de cuatro fases de 5B, el selector acotado de componentes de 5D-B2 y la identificación manual de 5D-C1. Crear una nueva tarea Meshy real sigue requiriendo autorización explícita del usuario.

## Materiales gráficos OBJ (5A)

ImportedMaterials recorre mallas/caras y GraphicsStyle para detectar referencias efectivas; GetMaterialIds puede estar vacío. En una familia nueva con una sola importación modifica únicamente materiales explícitos recién importados, protegiendo los IDs de plantilla. Para material implícito, UniformObjMaterial prepara una copia OBJ/MTL con directivas de material uniformes y conserva las líneas geométricas; se reimporta mediante el mismo motor y se borra la copia temporal. Se evita la asignación por categoría, que mezcló colores al cargar varias familias en RVT. ImportedMaterialDialog permite conservar, RGB o copiar color de un material disponible. PhotoFamilyWindow solicita el diálogo mediante el mismo ExternalEvent; FamilyFileWorkflow aplica y valida materiales tras reabrir antes de publicar con nombre único. Core/Meshy y algoritmos geométricos no cambian. No se crean parámetros Material ficticios ni overrides de vista. Véase stage5a-validation.md para evidencia RFA/RVT y límites de Realista/MTL.

## Materiales independientes OBJ (5C)

`ImportedMaterials.EditableMaterials` agrupa por `MaterialId` efectivo, no por nombres de grupos OBJ. El diálogo ofrece edición múltiple solo si hay al menos dos IDs materiales explícitos propios de la importación. `ApplyMultiple` comprueba referencias y protege materiales de plantilla; aísla los nombres de todos los materiales de esa importación para evitar que Revit fusione materiales homónimos al cargar varias familias en un RVT. Los materiales sin marcar conservan RGB, transparencia y sombreado. `Snapshot` registra nombres, RGB, propiedades gráficas y número de referencias; `FamilyFileWorkflow` lo compara después de reabrir el RFA, además de la validación geométrica habitual, antes de publicar. Los IDs solo se usan dentro del documento abierto; la persistencia se verifica mediante nombres aislados. Véase [stage5c-validation.md](stage5c-validation.md).
