# Arquitectura de BIM Family AI

## Componentes actuales

- **BimFamilyAI.Core**, net10.0: contratos de generación independientes del proveedor, estados/progreso y resultado OBJ; ObjImportRequest y ObjInspection existentes. Sin Autodesk, HTTP ni credenciales.
- **BimFamilyAI.Meshy**, net10.0: adaptador Image to 3D con entrada local, perfil económico T2, cliente HTTP asíncrono, seguimiento, descarga y diario JSON. Referencia solo Core. No añade paquetes externos.
- **BimFamilyAI.Revit**, net10.0-windows x64: tres comandos, geometría nativa, familia paramétrica e importación OBJ; único proyecto que conoce Autodesk. Referencia Core. Meshy aún no se conecta a este proyecto ni a su instalador.
- **Comprobaciones**: ejecutables Core.Checks y Meshy.Checks independientes de Autodesk; fixtures propios y transporte HTTP en memoria.

```text
Foto local -> LocalImage -> MeshyClient -> ID / estados -> OBJ local validado
                              |                              |
                      JsonTaskStore                    GeneratedObjResult
                              |                              |
                  LocalAppData/Simulated o Real       ObjImportRequest
                                                             |
                                         [coordinación UI/hilo: futura 4B]
                                                             |
                                              ObjFamilyImporter (Revit)
                                                             |
                                               FamilyFileWorkflow -> RFA
```

## Integración Meshy (4A)

LocalImage inspecciona contenedor, formato y dimensiones de JPG/JPEG, PNG y WebP y conserva bytes/hash para preparar Data URI en memoria. No sube fotos a un servidor público ni persiste base64. Es inspección de metadatos/estructura, no decodificación completa de píxeles. MeshyOptions expone únicamente el perfil compatible implementado: Smart Topology/T2, 4000 caras configurables entre 100 y 15000, sin texturas y OBJ. Más modelos o proveedores exigirán adaptadores/perfiles explícitos: no se añaden ahora interfaces sin uso.

CreateAsync valida, consulta la huella imagen+perfil y guarda una reserva antes del único POST. Guarda el ID inmediatamente al recibirlo. Intentos inciertos o rechazados permanecen bloqueados para evitar consumos duplicados. RecoverAsync reanuda por ID sin fotografía ni POST. QueryAsync/TrackAsync interpretan estados y progreso; esperan Retry-After o al menos 5 segundos. Solo GET tiene reintentos transitorios limitados. La cancelación es local: la tarea del proveedor puede continuar y el ID se conserva.

GetResultAsync reutiliza primero la copia propia validada por ObjInspection y SHA256. Si falta, obtiene un OBJ de una tarea exitosa mediante un cliente sin token. HTTPS y host exacto assets.meshy.ai, sin redirecciones; streams limitados a 50 MiB, temporales propios y publicación sin sobrescritura. Las URLs firmadas solo viven en memoria; la expiración permite una consulta adicional por ID, nunca una generación sustituta. Véanse [documentación y diferencias oficiales](stage4a-validation.md).

JsonTaskStore conserva exclusivamente metadatos seguros en LocalAppData/BIM Family AI/Meshy, separado por modo. Bloqueo de archivo entre procesos y reemplazo atómico en el mismo volumen. Un JSON corrupto se conserva y bloquea nuevas operaciones hasta reconciliarlo; no se descartan reservas. No almacena claves, fotos/base64 ni enlaces de descarga.

CreateSimulated recibe manejadores simulados y no construye transporte de red. CreateReal exige habilitación expresa; una creación nueva necesita además MESHY_API_KEY y una aprobación consumible ligada a imagen/configuración. No hay activación real desde los tres comandos. La futura interfaz deberá mostrar costo y obtener confirmación antes de construir esa aprobación. No se implementa cancelación remota.

GeneratedObjResult.ToImportRequest exige unidades/eje elegidos y altura opcional explícita; no deduce tamaño físico por fotografía. La importación sigue usando el motor 3B, sin un segundo conversor. La futura 4B debe coordinar espera asíncrona y ejecución de Autodesk mediante contexto válido, sin bloquear la UI ni usar Task.Run para Revit API.

## Funciones Revit conservadas

GenerateTestFamilyCommand crea una extrusión sólida de 500 mm. GenerateParametricFamilyCommand usa ParametricBoxBuilder: una extrusión, planos/cotas y fórmulas de semidimensiones para centrado, parámetros de tipo Ancho/Profundidad/Altura/Material, dos tipos y validación de flexión/material y reapertura. Ver [etapa 3A](stage3a-validation.md).

ImportObjFamilyCommand recoge unidades, eje y altura/nombre/descripción. ObjFamilyImporter usa importación nativa OBJ, escala uniforme con UnitUtils y CustomScale explícito, libera la fijación del elemento, rota, centra X/Y y apoya Z=0. Comprueba dimensiones, geometría y parámetros informativos. No reconstruye ni parametriza la malla. Ver [etapa 3B](stage3b-validation.md) para las convenciones de escala comprobadas y cobertura real.

FamilyFileWorkflow selecciona plantilla de Modelo genérico, crea documento independiente, ejecuta el generador/importador y guarda en temporal. Reabre familias paramétricas/OBJ para verificar persistencia y publica con sufijo sin sobrescribir en Documentos/BIM Family AI/Generated. Cierra documentos y comunica errores de limpieza. Es compartido por los tres comandos, sin cambios en 4A.

Límites OBJ conservados: 50 MiB, 250000 vértices, 500000 caras, 1024 índices/cara, 65536 caracteres/línea y coordenadas finitas hasta 1e8. El importador limita diagonal física a 0.1 mm–100 m y verifica centrado/base/dimensiones. ObjInspection es validación estructural; Autodesk puede rechazar una malla que la supere.

## Compilación, instalación y límites

Windows/Revit 2027.2 instalados; SDK 10.0.404 seleccionado por global.json desde 10.0.100 con latestFeature. Directory.Build.targets resuelve RevitInstallDir/REVIT_2027_DIR; referencias Autodesk con Private=false y error claro si faltan. El script existente instala solamente las DLL propias de Core/Revit y un manifiesto con tres GUID fijos. No se distribuyen DLL ni plantillas Autodesk.

La solución conserva configuraciones x64. Las dos advertencias MSB3277 de Microsoft.VisualBasic/System.Drawing provienen de dependencias de Autodesk y ya existían; no se ocultan. No hay backend, base de datos, cloud propio, OpenAI, Blender ni editor de imágenes. WPF se limita a los selectores/formulario existentes; la interfaz definitiva y la integración visible Meshy quedan para 4B, sin iniciar.
