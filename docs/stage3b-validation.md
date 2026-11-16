# Etapa 3B — Importación OBJ

## Diseño y uso

Tercer comando: **BIM Family AI — Importar OBJ como familia**, en Complementos → Herramientas externas. Seleccione un OBJ, indique obligatoriamente sus unidades (mm, cm, m, pulgadas o pies), eje vertical (Z por defecto, X o Y explícitos), altura objetivo opcional en mm, nombre y descripción. Cerrar o cancelar los diálogos cancela la operación.

`ObjInspection` en Core inspecciona progresivamente vértices y caras e índices positivos/negativos; no convierte ni reconstruye mallas. El importador especializado `ObjFamilyImporter` usa `Document.Import(string, OBJImportOptions, View)` de las DLL instaladas 27.2.0.39. Verifica `OptionalFunctionalityUtils.IsOBJImportLinkAvailable`. El ciclo existente de plantilla/documento/guardado/limpieza se extrajo a `FamilyFileWorkflow` y lo usan los tres comandos. Core no referencia Autodesk. La selección manual puede reemplazarse por un `ObjImportRequest` procedente de una futura integración IA sin reescribir el importador.

## Unidades, orientación y escala

OBJ no declara unidades fiables: se exige selección explícita. `feetPerUnit = UnitUtils.ConvertToInternalUnits(1, unidad seleccionada)`. Si se solicita altura, `factor = alturaObjetivoInterna / (alturaOriginal * feetPerUnit)`; de lo contrario factor=1. CustomScale siempre recibe `UnitUtils.ConvertFromInternalUnits(feetPerUnit * factor, Millimeters)`. Se establece Unit=Millimeter, pero CustomScale positivo la reemplaza, según el XML oficial instalado RevitAPI.xml. En pruebas con este módulo OBJ, Unit=Millimeter sin CustomScale importó 500 unidades como 5000 mm; un CustomScale de 0.00328084 produjo 1.640 mm. Por ello se usa conversión explícita en mm/unidad en todos los casos. No se deforma por ejes. La validación dimensional aborta ante cualquier diferencia de convención en otra instalación.

Orientación Z se conserva. Y vertical rota +90° alrededor de X y X vertical rota -90° alrededor de Y, mediante ElementTransformUtils. Se calculan dimensiones esperadas permutando los ejes correspondientes. Después se libera la fijación que Revit aplica al importado y se mueve por el centro X/Y y mínimo Z de su caja. Las dimensiones son extensiones de la caja alineada con ejes, no medidas intrínsecas de un objeto girado arbitrariamente.

La tolerancia es el mayor valor entre 0.1 mm y una millonésima de la diagonal. Se comprueban dimensiones, centro, base y presencia de Mesh con triángulos o Solid con caras. El resultado debe ser una importación embebida 3D, no un vínculo ni un elemento exclusivo de una vista. Tras guardar, se cierra y reabre el RFA para comprobar categoría, importación, geometría, parámetros y posición antes de entregar el archivo final sin sobrescribir.

## Datos BIM y materiales

Parámetros de tipo informativos: Nombre del modelo, Descripción, Archivo de origen (solo nombre), Unidades originales (texto); Ancho, Profundidad y Altura son longitudes reales. **No controlan la malla** ni se asocian a restricciones. No se crea Material ficticio. Materiales/MTL se delegan al importador oficial con colores conservados. MTL y texturas no se exigen para geometría básica. No se promete fidelidad de texturas o UV.

## Límites

Máximo 50 MiB, 250000 vértices, 500000 caras, 1024 índices por cara y 65536 caracteres por línea. Coordenadas finitas con valor absoluto hasta 1e8 unidades de origen. Diagonal final entre 0.1 mm y 100 m. Se permiten superficies planas si tienen al menos dos extensiones no nulas; líneas y puntos se rechazan. Una malla corrupta que pase la inspección estructural todavía puede ser rechazada por Autodesk. No se reparan ni optimizan mallas.

La lectura es progresiva y tiene memoria limitada por esos límites. Las llamadas nativas ocurren sin hilos secundarios dentro del comando Revit. Autodesk no ofrece cancelación/timeout a mitad de esta llamada síncrona de importación: una malla difícil puede mantener ocupada la interfaz; no se garantiza un tiempo máximo de importación.

## Resultados registrados

- Base anterior compilada antes de cambios: cero errores, dos MSB3277.
- Solución actual .NET SDK 10.0.404, net10.0-windows x64: cero errores, dos MSB3277 sin suprimir. Conflictos de Microsoft.VisualBasic y System.Drawing entre referencias .NET y dependencias de Autodesk; ya estaban en la base y no se añadieron paquetes.
- Once comprobaciones Core ejecutadas con `dotnet run --project tests/BimFamilyAI.Core.Checks -- tests/fixtures`: pasan los límites de caja de ambos modelos, índices negativos y ocho casos de rechazo, incluidos vacío, sin caras, NaN, infinito, índice fuera de rango, tamaño nulo, tamaño excesivo y ruta inexistente.
- Instalación ejecutada, tres comandos visibles en Revit 2027.2.
- Primera importación detectó que Autodesk fija el elemento; MoveElement falló con mensaje de elemento fijado. Se corrigió liberando Pinned antes de rotación/traslación. El fallo no produjo RFA final.
- Las pruebas detectaron además dos convenciones de escala: CustomScale interpretado respecto a mm y Unit ignorada por este módulo OBJ sin escala personalizada (500 unidades importadas como 5000 mm). Se usa finalmente CustomScale explícito en mm por unidad, con conversión de UnitUtils. Los resultados incorrectos se rechazaron y no dejaron RFA de entrega.
- Con la DLL final instalada, A/B/C pasaron en Revit 2027.2: 500/500/500, 1000/600/750 y 2000/1200/1500 mm, respectivamente. En los tres se validaron caja, centrado, Z=0, geometría y reapertura mediante API.
- Resultados: `cube_500mm.rfa`, `offset_prism_m.rfa`, `offset_prism_m_1.rfa`, todos de 454656 bytes en Documentos/BIM Family AI/Generated. C no sobrescribió B.
- Los tres se generaron desde el proyecto propio previamente guardado BFAI_Stage3A_Validation_20261115. Se ejecutaron también los generadores fijo y paramétrico: crearon TestCube_500mm_4.rfa y ParametricBox_3.rfa con sus validaciones superadas. Después el proyecto se cerró directamente sin solicitar guardar cambios: prueba G superada por interfaz, sin leer IsModified por API.
- Independencia: se renombró temporalmente el fixture propio `offset_prism_m.obj` a `.obj.unavailable`, comprobando que no existía la ruta original. Se abrió `offset_prism_m_1.rfa`, se visualizó la geometría en 3D sombreada y se inspeccionaron los parámetros Altura=1500, Ancho=2000 y Profundidad=1200. Se cargó en el proyecto de prueba y se colocó una instancia `Modelos genéricos: offset_prism_m_1: Importado`, sin restaurar aún el OBJ. Prueba E superada. Después se restauró el fixture y se cerró el proyecto sin guardar la carga de ensayo; la familia permanece abierta en 3D.
- No se probaron en Revit los ejes verticales X/Y, centímetros, pulgadas y pies, ni MTL/texturas. No se simularon instalación sin módulo OBJ, falta de permisos o mallas próximas a los límites. Son ampliaciones de cobertura pendientes; no se atribuyen resultados a esos casos.

## Matriz de pruebas

| Caso | Entrada y configuración | Resultado esperado (mm) |
|---|---|---|
| A | cube_500mm.obj, milímetros, Z, sin altura objetivo | 500 × 500 × 500 |
| B | offset_prism_m.obj, metros, Z, sin altura objetivo | 1000 × 600 × 750 |
| C | offset_prism_m.obj, metros, Z, altura objetivo 1500 mm | 2000 × 1200 × 1500 |

Los dos OBJ de fixtures son geometría creada para esta prueba sin materiales propietarios. B tiene origen (10,20,30) m. En todos los casos se espera centro X/Y=0 y base Z=0. Para comprobar independencia, después de generar cierre el RFA, cambie temporalmente el nombre del OBJ propio de prueba, reabra el RFA, inspeccione 3D y cárguelo en un proyecto de prueba; restaure el nombre del OBJ al terminar. No renombre ni borre archivos ajenos.

Ejecute ambos comandos anteriores para regresión. Para el proyecto activo, use un proyecto de prueba guardado, importe y cierre sin guardar: Revit no debe solicitar guardar cambios. No confundir esta prueba con cargar la familia, que sí cambia deliberadamente el proyecto.

## Estado

Etapa 3B completada para la conversión básica probada (Z vertical, milímetros/metros y altura objetivo). Los criterios 1–15 se comprobaron en ese alcance mediante compilación, pruebas del lector y ejecución real en Revit; las variantes no ensayadas se enumeran arriba. Preparada para recibir archivos de una futura integración mediante ObjImportRequest; antes de usar mallas reales de IA conviene ampliar la cobertura con esos modelos. Etapa 4 no iniciada.
