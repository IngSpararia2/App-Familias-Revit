# Etapa 5A — Materiales gráficos persistentes en OBJ/RFA

## Alcance y mecanismo

No se modifica la representación OBJ, escala, orientación, parámetros dimensionales ni algoritmos del importador. No se llama a Meshy ni se cambia su perfil, credenciales, caché o historial. El RFA original de la silla encontrado es `Silla_Comedor_Linda_Corregido.rfa` (el nombre local difiere del indicado en el pedido).

Fuentes oficiales consultadas el 16/11/2026:

- [Materiales en OBJ importados](https://www.autodesk.com/support/technical/article/caas/sfdcarticles/sfdcarticles/How-are-materials-assigned-to-Revit-families-during-an-OBJ-import.html): la información reside en el objeto y puede no existir un parámetro Material.
- [Material en geometría CAD importada](https://www.autodesk.com/support/technical/article/caas/sfdcarticles/sfdcarticles/How-to-apply-the-material-to-imported-CAD-geometry-in-Revit-family.html): material por capa en estilos de objetos.
- [Estilos de objetos Revit 2027](https://help.autodesk.com/cloudhelp/2027/ENU/Revit-Customize/files/GUID-30290298-D23B-40FC-8642-050C3BD1830D.htm).
- RevitAPI.xml de la instalación 2027: Category.Material es modificable; Mesh.MaterialElementId y GraphicsStyleId permiten inspeccionar referencias. GetMaterialIds(false) puede devolver vacío si la categoría no admite cantidades de materiales.

## Experimento previo a la implementación

Se ejecutó un complemento local de diagnóstico en Revit 2027.2, en Idling/contexto API, sobre copias propias: cubo sin MTL, cubo con dos regiones MTL y OBJ de silla copiado desde la caché. No se editó el OBJ original.

| Modelo | Mallas/materiales | Categorías y parámetros |
|---|---|---|
| Cubo | 12 triángulos, MaterialElementId=-1 | Importaciones en familias / Categoría por defecto, GraphicsStyle válido; sin MATERIAL_ID_PARAM |
| Dos regiones | dos mallas de 6 triángulos, materiales Red y Green distintos | Una categoría por defecto compartida; los grupos OBJ no generaron capas editables independientes |
| Silla | 4172 triángulos, MaterialElementId=-1 | Mismo mecanismo por categoría, sin MATERIAL_ID_PARAM |

GetMaterialIds devolvió vacío para los tres: la detección definitiva recorre GeometryInstance, Mesh/Face y GraphicsStyle, no depende de ese método. El material azul asignado a la categoría persistió al guardar/reabrir; se inspeccionó visualmente el cubo azul en la vista 3D sombreada guardada. Los materiales explícitos del MTL también pudieron editarse y persistieron.

La estrategia A por categoría se descartó: funcionó en RFA aislados, pero Revit fusionó la categoría por defecto al cargar varias familias en RVT y mezcló colores. Category.Name es de solo lectura y GraphicsStyle.Name rechazó el cambio en ejecución. La estrategia elegida usa materiales explícitos: editar solamente materiales creados durante esa importación en la familia nueva. Para OBJ sin material explícito, UniformObjMaterial crea una copia temporal con MTL uniforme, modifica solo mtllib/usemtl y conserva las líneas de vértices/caras. La importación propia se reemplaza por esa copia usando exactamente ObjFamilyImporter y su escala/eje originales; no se reconstruye geometría ni se cambia de representación. La copia conserva el nombre de archivo de origen y se elimina al acabar. Los IDs que existían antes de importar están protegidos: si la malla comparte uno de la plantilla, la operación se rechaza y se ofrece conservar la apariencia. No hay DirectShape, reconstrucción de malla, Paint ni OverrideGraphicSettings.

## Implementación y uso

`ImportedMaterials` concentra inspección, asignación y validación; `ImportedMaterialDialog` reúne las opciones. Core y Meshy no conocen Autodesk ni cambiaron. `PhotoRevitRequest` transporta únicamente la decisión de abrir el diálogo mediante el ExternalEvent existente. `FamilyFileWorkflow` reutiliza importación, guardado sin sobrescribir, reapertura y validación geométrica; añade comprobación RGB efectiva al reabrir. Los tres comandos anteriores conservan su comportamiento.

1. Abra el cuarto comando, recupere un OBJ válido del historial y confirme altura/eje/nombre. No pulse Generar para cambiar un material.
2. En la sección de salida, marque **Materiales y apariencia: elegir antes de guardar** y pulse Crear familia RFA.
3. En el diálogo puede conservar original, aplicar RGB uniforme o copiar el color gráfico de un material que realmente existe en la familia de destino. La última opción copia el RGB; no vincula la malla a todas las propiedades del material seleccionado ni modifica ese material.
4. Indique nombre y RGB de 0 a 255 separados por comas; por ejemplo azul `0,40,255` o rojo `240,20,20`. Continúe para guardar un RFA nuevo, o cancele sin publicar un RFA.
5. Abra el resultado, elija Sombreado y revise el material en Gestionar → Materiales / Estilos de objetos importados. Puede volver a Crear RFA con otro color utilizando el mismo OBJ.

Por defecto la casilla queda desmarcada y no se crean materiales adicionales. Los materiales propios tienen prefijo BFAI, nombre identificable y RGB; las regiones explícitas conservan IDs separados y reciben sufijos. Las colisiones de nombres se rechazan sin modificar materiales existentes; no se acumulan materiales por cada cara. El cambio afecta al nuevo documento de familia, no a los RFA previos ni bibliotecas externas. No se añaden parámetros Material ficticios.

El nivel básico uniforma el color gráfico; no deduce asiento/respaldo/patas. `Region(MaterialId, CategoryId)` conserva referencias detectadas para futuras asignaciones múltiples. El fixture `material_regions.obj` con `multi.mtl` permite probar dos materiales separados: usemtl/MTL sí identifica regiones; un nombre de grupo sin referencia distinguible no garantiza separación en Revit.

## Apariencia y límites

Se configura Material.Color, Transparency=0 y UseRenderAppearanceForShading=false. Es un material persistente del documento, no una sobrescritura de vista. No se configuran apariencia de renderizado, UV, texturas ni propiedades físicas/térmicas. Realista utiliza recursos de apariencia y puede verse diferente del RGB de Sombreado; no se promete coincidencia ni calidad de render. Copiar un RGB tampoco copia texturas ni activos de renderizado.

No se implementa edición de RFA arbitrarios existentes: se crea una familia nueva desde el OBJ conservado. Esto evita alterar materiales compartidos de documentos ajenos. Si no hay referencias editables, la aplicación falla con mensaje explícito y no publica un RFA de éxito; puede repetir conservando original.

## Pruebas y reproducción

El ejecutable de integración opcional `tests/BimFamilyAI.Revit.MaterialChecks` enlaza los mismos componentes de producción y solo se ejecuta dentro de Revit. No forma parte del instalador ni del arranque normal. Su registro temporal IExternalApplication se retira tras las pruebas; no debe dejarse instalado. Requiere el OBJ de silla copiado a `artifacts/stage5probe/chair.obj` y las plantillas métricas locales. Cada ejecución crea una carpeta artifacts/stage5a con fecha, RFA nuevos, RVT y results.json; no sobrescribe resultados anteriores.

El primer diagnóstico dejó `artifacts/stage5probe/report.json`. La primera validación aprobó 26 comprobaciones RFA/regresión y detectó que la prueba LoadFamily debía estar dentro de una transacción. Se corrigió únicamente el ejecutable de pruebas y se repitió, añadiendo material uniforme sobre MTL. Los resultados definitivos se registran al final de este informe.

Compilación de la solución Release/x64: 0 errores, 2 MSB3277 preexistentes (Microsoft.VisualBasic/System.Drawing), sin suprimir. Core: 11 aprobadas. Meshy: 187 aprobadas, transporte completamente simulado. No se añadieron paquetes ni dependencias de terceros.

## Texturas futuras (sin implementar)

Una intervención posterior deberá verificar el contrato oficial Meshy y descargar localmente materiales/texturas junto al modelo antes de que expiren sus enlaces. OBJ necesita MTL y rutas de imágenes válidas; otros contenedores como GLB pueden empaquetar más información, pero no se comprobó su importación nativa ni se propone cambiar el flujo ahora. Revit puede conservar materiales MTL en el caso probado; UV, texturas de Meshy, apariencias Realistas y múltiples acabados reales requieren pruebas propias. Se mantiene T2/smart-topology, 4000, should_texture=false. No se generaron texturas pagadas ni se regeneró la silla.

## Archivos

Nuevos: ImportedMaterials.cs, ImportedMaterialDialog.cs, UniformObjMaterial.cs, proyecto opcional Revit.MaterialChecks y su ejecutor, fixtures material_regions.obj/multi.mtl, este informe. Modificados: FamilyFileWorkflow.cs, PhotoFamilyExternalEvent.cs, PhotoFamilyWindow.cs, README.md y docs/architecture.md. No se modifican Core, Meshy, importador geométrico, instalador ni GUID. Sin commits, cambios remotos ni etapa 5B.

## Resultados finales

**Etapa 5A completada para material gráfico persistente y color en Sombreado.** Se descartaron expresamente las variantes que mezclaban colores. La última ejecución real en Revit 2027.2 (`artifacts/stage5a-20261116-145711/results.json`) aprobó **47 comprobaciones, sin errores**.

| Prueba | Resultado verificado |
|---|---|
| A/B | Cubos azul y rojo: material explícito, geometría idéntica, plantilla intacta, RGB persistente tras guardar/reabrir. |
| C | Conservar original: ningún material adicional; resultado gris en el proyecto. |
| D | OBJ/MTL: dos referencias materiales distintas; modificar una conservó la otra. Ambos IDs persistieron. Uniformar ambas produjo RGB rojo en Sombreado. |
| E | Cada RFA pasó ValidateSaved: Modelo genérico, una importación 3D, dimensiones, centrado/base y geometría. La firma de vértices/número de triángulos coincidió antes/después del material y al reabrir. |
| F | Se cargaron/colocaron seis familias en RVT propio, guardado y reabierto. Se comprobaron colores efectivos de las mallas colocadas: azul 0,40,255; rojo 240,20,20; ambas regiones uniformes rojas y silla azul. Inspección visual confirmó azul/rojo/gris sin mezcla. |
| G | Copia del OBJ local de la silla, eje Y, altura de ensayo 750 mm: material azul y geometría conservados, RFA reabierto y silla colocada azul en RVT. No se regeneró IA. |
| H | Generadores fijo/paramétrico, importación manual y contrato fotográfico simulado ejecutaron el flujo compartido dentro de Revit. El paramétrico validó flexión/material y reapertura con sus aserciones originales. No se atribuye a esto una repetición manual de los cuatro menús. El cuarto comando sí se recorrió desde la interfaz actualizada. |

Realista se abrió y observó en `Stage5A_Realistic`: los cubos preparados con MTL y la silla mostraron color; el caso de dos regiones uniformado solo gráficamente conservó rojo/verde de su apariencia MTL original. Por ello el alcance garantizado es Sombreado. No se crean/editan activos de renderizado ni se promete Realista uniforme. Los materiales son persistentes en el documento; la diferencia entre vistas proviene de material gráfico frente a apariencia, no de overrides.

Prueba de interfaz completa: cuarto comando → historial simulado → Continuar (OBJ local, POST=0, GET=0, descargas=0) → altura 750 → casilla Materiales y apariencia → Crear RFA → RGB 0,80,220 → nuevo `SIM_Familia_Foto_2.rfa`. Segunda ejecución con copiar color del material disponible «Cubierta por defecto» → `SIM_Familia_Foto_3.rfa`. En ambos el flujo confirmó guardado/reapertura y validación del material efectivo. No se sobrescribieron resultados previos. Los temporales BFAI-Material se eliminaron al finalizar estas operaciones.

Los experimentos previos que no pasaron permanecen como evidencia: carga sin transacción y cambio de ActiveView en arranque fueron corregidos en el ejecutor; asignar material a la categoría compartida produjo mezcla de colores y se descartó; Category.Name de solo lectura y GraphicsStyle.Name no modificable se descartaron. La solución final no intenta renombrar esas categorías ni estilos. Se añadieron comprobaciones de RGB en instancias colocadas para detectar específicamente esa regresión.

El registro temporal `BFAI_Stage5Checks.addin` se retiró: no se ejecutarán pruebas al próximo arranque. El complemento normal quedó actualizado con sus cuatro comandos y GUID, sin dependencias nuevas. Compilación final de solución y proyecto de integración: 0 errores y 2 MSB3277 preexistentes cada uno; Core=11 y Meshy=187 aprobadas. `git diff --check` sin errores (solo aviso Git LF/CRLF). No se publicaron commits ni cambios remotos.

El RFA original `Silla_Comedor_Linda_Corregido.rfa` conserva SHA256 **F6C7BF555C4B09D280D80824062D685E2C06D20B692965FFE18815037E614463**. No se editaron ni borraron OBJ/RFA originales o diarios Meshy. **Cero solicitudes de producción Meshy, cero nuevas generaciones y cero créditos consumidos durante 5A.**

### Archivos de ensayo para revisión

Carpeta local `artifacts/stage5a-20261116-145711`: Stage5A_Blue.rfa, Stage5A_Red.rfa, Stage5A_Original.rfa, Stage5A_Multi.rfa, Stage5A_MultiUniform.rfa, Stage5A_Chair.rfa y Stage5A_Materials.rvt. El proyecto incluye vistas Stage5A_Shaded y Stage5A_Realistic. No distribuir los ensayos anteriores con mezcla de categorías como resultados finales.

### Acciones manuales y limitaciones

No queda una acción necesaria para activar el material gráfico: el complemento está instalado. Para su silla, recupere el OBJ local de la tarea existente, confirme su altura medida y eje Y, indique un nombre nuevo, marque Materiales y apariencia, elija RGB y cree/abra el nuevo RFA. Use Sombreado. No pulse Generar modelo para cambiar un color.

La edición de materiales múltiples desde UI, texturas/UV, apariencia Realista uniforme y propiedades físicas/térmicas no están implementadas. El aislamiento se ha probado en el RVT nuevo indicado; no se garantiza sustituir automáticamente materiales homónimos en proyectos ajenos ya poblados. Si la importación usa materiales preexistentes de la plantilla se rechaza su modificación. No se implementó un editor de RFA arbitrarios ni un conversor geométrico alternativo. Etapa 5B sin iniciar.
