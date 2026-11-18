# Etapa 5D-B2 — ensamblaje OBJ en una familia Revit

## Estado y alcance

Se partió de `main` en `c089366a641226c9db1f8e33f33860ce545c4974`, con árbol limpio. Se conservaron el importador simple, los cuatro comandos, GUID e instalador. No se cambiaron los OBJ/RFA anteriores ni se creó ninguna tarea Meshy. La prueba de silla usó `artifacts/stage5probe/chair.obj` ya disponible localmente. Esta etapa crea importaciones OBJ independientes dentro de un único RFA; no reconoce nombres semánticos ni convierte mallas en sólidos nativos.

## Implementación

- `ObjComponentExtractor.Verify` vuelve a analizar el OBJ origen y comprueba SHA256, versión, número/orden de piezas, MTL requeridos, hashes, índices de caras y vértices de origen, datos geométricos de cada pieza y cajas. Una pieza ausente o alterada impide importar.
- `ObjAssemblyTransform` (Core) define una sola transformación afín desde la caja del OBJ completo: Z original `(x,y,z)`, Y original `(x,-z,y)`, X original `(-z,y,x)`; escala uniforme con unidades o altura objetivo; traslación para centrar X/Y y apoyar el mínimo Z en cero. La misma transformación se aplica a todas las piezas. No se centra ni escala ninguna pieza aisladamente.
- `ObjAssemblyImporter` importa las piezas en orden de manifiesto dentro de una sola transacción y un documento nuevo de Modelo genérico. Cada pieza es una `ImportInstance` embebida. Extensible Storage guarda ID `component_###`, SHA256 del origen y SHA256 de la pieza en el elemento; los comentarios de instancia se rellenan cuando Revit los deja editar. No cambia categorías globales.
- Las piezas con RGB elegido pasan por copias OBJ/MTL temporales derivadas; `ImportedMaterials.InspectElement` exige una referencia material propia, distinta de la plantilla y de otras piezas. Se asignan nombres de material únicos por familia y componente, RGB en Sombreado y transparencia cero. Las piezas sin selección conservan la apariencia de importación. No se escriben los OBJ originales ni se prometen texturas o apariencia Realista.
- `FamilyFileWorkflow` mantiene la ruta simple y añade una rama de ensamblaje. La validación comprueba geometría no vacía, número/identidad de importaciones, cajas de **cada pieza**, caja global, dimensiones BIM, altura, orientación y RGB tras reabrir el RFA. La tolerancia es el mayor de 0,1 mm o una millonésima de la diagonal final, para absorber precisión del importador nativo de Revit. La publicación sigue usando un nombre nuevo sin sobrescribir.
- En el cuarto comando de Revit, fase Preparación BIM, se elige expresamente OBJ completo o Componentes separados. Puede seleccionarse un OBJ local ya existente o recuperarse el del historial. El análisis muestra la cantidad topológica y un diálogo permite RGB por componente; una sola componente obliga a usar el modo completo. La extracción local se hace fuera del hilo API; la importación se entrega a `ExternalEvent` en contexto Revit.

Se contrastaron `Document.Import`, `ElementTransformUtils.MoveElement/RotateElement`, `Element.SetEntity/GetEntity` y los materiales con el XML de `RevitAPI.dll` instalado en Revit 2027. La [ayuda oficial de Autodesk 2027 sobre formas importadas](https://help.autodesk.com/cloudhelp/2027/ENU/Revit-Model/files/GUID-AD4862C9-F714-4FF0-8C7F-351BB679D0C2.htm) indica el tratamiento como elementos importados; las pruebas API de esta etapa verificaron seis `ImportInstance` independientes dentro de la familia.

## Silla Meshy T2 existente

El OBJ real local tiene 2195 vértices, 4172 caras y **seis** componentes topológicos. Su `manifest.json` y los seis OBJ se volvieron a generar para la prueba, conservando las 4172 caras. El OBJ no declara `g` ni `usemtl`; las componentes no equivalen automáticamente a asiento, respaldo o patas. Con Y como eje vertical original y altura confirmada de 750 mm, la caja global esperada es **403,68975 × 459,47625 × 750 mm**, centrada en X/Y y apoyada en Z=0. `ValidateSaved` contrastó la caja global y las seis cajas individuales con el resultado de Revit antes y después de reabrir. Los seis elementos persistieron como importaciones embebidas, no vinculadas.

La prueba API asignó RGB (240,20,20) a `component_001` y (0,40,255) a `component_002`; una segunda familia de la misma silla usó (0,180,40) y (220,180,0) en esos componentes. Se cargaron ambas en un RVT, se colocaron, se guardó/reabrió el RVT y se verificaron los cuatro RGB a través de los materiales de las mallas de las instancias. La prueba de interfaz eligió el mismo OBJ local y asignó rojo a `component_001` y azul a `component_003`; el RFA se creó, verificó y abrió desde la ventana. En Sombreado se ve el cuerpo rojo y el respaldo azul. La selección en Revit mostró cajas distintas para dos importaciones individuales.

## Pruebas ejecutadas el 17/11/2026

| Prueba | Resultado |
|---|---|
| Solución Release/x64, .NET 10 / Revit 2027.2 | 0 errores; 2 advertencias MSB3277 preexistentes por versiones de dependencias Autodesk. |
| Core.Checks | **113/113**. Incluye ejes X/Y/Z, escala, unidad, centrado/base global, manifiesto válido, hashes alterados y pieza faltante. |
| Meshy.Checks | **187/187** con transporte HTTP simulado; **0 POST de generación real, 0 créditos consumidos en esta etapa**. |
| Revit API, `AssemblyChecks` temporal | **20/20**: silla de seis piezas, segunda familia, cajas por elemento, ejes X/Y/Z con dos piezas desplazadas, importación simple 3B, familia paramétrica 3A, guardado/reapertura RFA y RVT, colores independientes entre familias. Resultado: `artifacts/stage5d-b2-20261117-210119/results.json`. |
| Computer Use en Revit 2027.2 | Ejecutado. Se abrieron RFA/RVT, se observó la silla en Sombreado desde frente y lateral, se seleccionaron dos importaciones independientes, se comprobaron los cuatro comandos, se creó otro RFA de seis piezas mediante el diálogo y se observaron dos familias con colores distintos en el RVT. |

Evidencias locales: `artifacts/stage5d-b2-20261117-210119/chair-3d-shaded.png`, `chair-side-shaded.png`, `chair-back-selected.png`, `chair-ui-red-blue.png` y `chairs-rvt-shaded.png`. El RFA de la interfaz está en `Documentos/BIM Family AI/Generated/Stage5D_B2_UI_Chair.rfa`. El proyecto de prueba está en `artifacts/stage5d-b2-20261117-210119/Stage5D_B2_Assembly.rvt`. Estos artefactos están fuera del código versionado.

No se repitió en esta sesión la batería histórica completa de materiales 5A/5C dentro de Revit: `ImportedMaterials` conservó sus operaciones existentes y añadió solo inspección por elemento; la prueba nueva comprobó materiales independientes y dos familias sin contaminación. Tampoco se ensayó una familia con más de 128 componentes ni texturas.

## Límites y siguiente etapa

Core valida sintaxis/topología, pero Revit puede rechazar una pieza aunque pase Core; entonces se aborta toda la familia. La coincidencia geométrica se comprueba con cajas por pieza y existencia de malla, no con una comparación triángulo a triángulo después de la triangulación interna de Revit. El manifiesto B1 sí valida caras e índices exactos antes de importar. El color gráfico se garantiza en Sombreado; Realista y texturas permanecen fuera de alcance. Las piezas que comparten vértices siguen siendo una sola componente. Los OBJ no aportan unidades físicas fiables: el usuario confirma altura y eje. Cancelar o cerrar la ventana antes de encolar conserva el OBJ; una importación en curso usa el contexto síncrono válido de Revit.

Para 5D-C haría falta decidir con evidencia cómo segmentar una malla conectada y cómo asignar nombres semánticos verificables, sin prometer piezas nativas paramétricas a partir de esta extracción topológica. No se inició esa etapa.
