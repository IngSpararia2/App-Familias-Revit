# Validación multióbjeto de 5D-C2B

Se ejecutó la batería del 23 de noviembre de 2026 sobre copias locales bajo `artifacts/stage5d-c2b-20261123`. El historial se consultó en `%LOCALAPPDATA%/BIM Family AI/Meshy/Real/tasks.json`. Allí se guardan los archivos con nombres generados por la aplicación. Los identificadores Meshy se cotejaron con sus rutas, fechas y SHA256, y se conservaron localmente. Los nombres de objetos se asignaron tras inspeccionar las proyecciones de la geometría con Y como vertical para las descargas y la silla. [Proyecciones de orientación](../artifacts/stage5d-c2b-20261123/orientacion.png)

## Inventario y diagnóstico

Hora local de Bogotá. Cada fila indica creación registrada y modificación del archivo descargado. Las dimensiones son extensiones geométricas de origen en X/Y/Z.

| Modelo e identificación visual | Archivo original | Creación → descarga | Bytes | Vértices / caras / componentes | Extensiones X/Y/Z |
|---|---|---|---:|---:|---|
| Frasco cilíndrico con junta visible entre dos tramos | `519073522a5443ad9668549bb960bd4c.obj` (`meshy-3`) | 20:34:10 → 20:34:21 | 302140 | 2153 / 4270 / 2 | 0.320831 / 1 / 0.318994 |
| Zapato con suela perfilada, abertura, lengüeta y formas de cordones | `f63c1cc15b524997b16e2531dc43e747.obj` (`meshy-1`) | 20:33:26 → 20:33:37 | 316345 | 2252 / 4259 / 29 | 1 / 0.514874 / 0.403383 |
| Florero de cintura estrecha y cavidad visible, con salientes en la abertura e interior | `b145812b55c24a5097f1a89c5dc9fd6d.obj` (`meshy-2`) | 20:33:53 → 20:34:04 | 314110 | 2185 / 4305 / 9 | 0.389928 / 1 / 0.387615 |
| Máquina 1, base prismática, bandeja, travesaños, soportes y panel elevado | `32325249ea9f4d9490a1d1f84433a93a.obj` (`meshy-4`) | 20:34:30 → 20:34:41 | 597679 | 4369 / 7830 / 148 | 0.844739 / 1 / 0.685569 |
| Máquina 2, dos cuerpos prismáticos, estructura vertical, panel inclinado y formas tubulares | `b17336d02b794e9b95c789df657789ae.obj` (`meshy-5`) | 20:34:46 → 20:34:57 | 622849 | 4466 / 8199 / 134 | 0.997588 / 0.997007 / 0.709965 |
| Silla Meshy original | `artifacts/stage5probe/chair.obj` | 16/11, 14:21:45 → 14:21:51, descarga del mismo hash | 310501 | 2195 / 4172 / 6 | 0.538253 / 1 / 0.612635 |
| Cubo controlado | `tests/fixtures/cube_500mm.obj` | Fixture | 240 | 8 / 12 / 1 | 500 / 500 / 500 mm |

| Modelo | SHA256 | Aristas abiertas / no manifold / orientación incoherente |
|---|---|---:|
| Frasco | `6b3851b6e5cdf66318803ebaf83089c4e5399892cd4dd9e57401b2647f3cbe60` | 32 / 0 / 0 |
| Zapato | `dddfcea1e6a74287c045fd1cf5694339e6722624bfb1f829a5178b0cd8f54615` | 149 / 0 / 3 |
| Florero | `fa5c00916d643d3d65a291e8b15eb258c21010336cd59fdbe3f53f501105ff49` | 31 / 0 / 0 |
| Máquina 1 | `2a5cefea202a255749852f319508d39f3394313626a42319e596bcad2e5ac728` | 396 / 8 / 0 |
| Máquina 2 | `d80487129141335781de5f4ef9cd8ddad1173b7b171aaefd704f8f709ffeccb5` | 281 / 0 / 0 |
| Silla | `4ced7c29db566d1f48876bd88646a41f1134e39dc3e1f660a40419297bee18be` | 226 / 2 / 0 |
| Cubo | `e59d875527ccaaac23e89bb583e482b4c5a927f22e5fa6ce54bdb218e9fdc949` | 0 / 0 / 0 |

Las seis mallas Meshy son triangulares y declaran `o mesh_node`, `mtllib model.mtl` y normales. No declaran grupos, coordenadas UV ni `usemtl`. El cubo contiene solo posiciones y caras. No se detectaron caras degeneradas con la prueba de área relativa de la batería. Los conteos de borde y orientación usan los índices OBJ. Las aristas no manifold detienen el crecimiento y los bordes entre índices separados pueden interrumpirlo aunque aparezcan unidos en la vista. La abertura y las formas interiores del florero pertenecen al OBJ fuente. [Diagnóstico de topología](../artifacts/stage5d-c2b-20261123/topologia.json)

## Correspondencia, regiones y planes

En cada caso se verificó B1, la correspondencia de todos los triángulos de vista previa con sus caras y componentes, y la derivación C2A mediante sus verificaciones de vértices, coordenadas, normales, esquinas, orden de caras y materiales. Se guardaron, reabrieron y modificaron planes sin sobrescribir. Las pruebas Core comprobaron el rechazo de un plan de otro OBJ y el de origen alterado. Se comparó el crecimiento a 0°, 6°, 25° y 60° desde caras alcanzadas por rayos de vistas ortográficas. Las tres muestras técnicas de cada modelo, en orden de sus tamaños en la tabla, usan azul `0,90,225`, rojo `220,56,33` y verde `20,160,91`, con gris `150,150,150` en el resto de caras sin material de cada pieza editada. [Rayos, componentes, muestras y tiempos](../artifacts/stage5d-c2b-20261123/core-models.json)

| Modelo | Caras por muestra a 6° | Comportamiento y límite de la selección |
|---|---:|---|
| Frasco | 235 / 7 / 2 | Las muestras alcanzaron las dos piezas. Una semilla pasó de 235 caras a 1884 al subir a 25°. |
| Zapato | 3 / 1 / 1 | Se alcanzaron tres piezas. Una cara de la pieza principal pasó de 1 a 1059 a 25°. La suela necesita confirmación visual. |
| Florero | 1 / 34 / 18 | Se alcanzaron dos piezas. La semilla de una pared pasó de 34 caras a 680 a 25°. La separación visual de interior y exterior sigue siendo necesaria. |
| Máquina 1 | 2 / 1 / 2 | Tres piezas alcanzadas con vista superior y laterales. El crecimiento a 6° conservó los detalles locales. |
| Máquina 2 | 1 / 2 / 7 | Tres piezas alcanzadas. La tercera muestra aumentó a 11 caras a 25°. |
| Silla | 283 / 26 / 20 | Las tres muestras pertenecen a `component_001`. Son selecciones técnicas. La vista identifica el asiento y el respaldo, y la selección de cojín requiere validación en el editor. |
| Cubo | 2 / 2 / 2 | Tapa y dos laterales. El crecimiento se mantuvo en dos caras entre 0° y 60°. |

El análisis y la extracción tardaron de 38 ms a 2.09 s. La verificación y preparación de la vista previa tardaron de 16 a 609 ms. Esos tiempos corresponden al ejecutor Core, antes de dibujar en WPF. [Mediciones](../artifacts/stage5d-c2b-20261123/core-models.json)

## Correcciones reproducidas

- El editor aceptaba un plan vacío con una región pendiente y al cambiar de componente borraba nombre y selección. Ahora conservará el borrador hasta que el usuario lo aplique o descarte explícitamente. Guardar, cargar y usar regiones muestran el aviso en la parte fija de la barra lateral. Deshacer calcula el resaltado a partir de las caras efectivas. El botón de aplicar respaldo conserva el borrador de región. [Reproducción WPF](../artifacts/stage5d-c2b-20261123/editor-baseline.log) · [Repetición WPF](../artifacts/stage5d-c2b-20261123/editor-after.log)
- Aplicar un respaldo a una pieza sin regiones dejaba su RGB fuera de la vista y del plan. El plan admite un respaldo explícito para una pieza con caras sin material, incluso si la pieza no contiene regiones. Las caras con `usemtl` tienen prioridad. Core verificó el RGB exacto en la derivación y la retirada del respaldo del plan. [Regresión Core](../artifacts/stage5d-c2b-20261123/core.log)
- La compilación de referencia rechazó ambas máquinas en el ensamblador por el límite de 128 piezas. La ruta de regiones acepta hasta 256 componentes y puede leer las identidades de ese intervalo. [Reproducción en Revit 2027.2](../artifacts/stage5d-c2b-20261123/api-205133/results.json)
- El gesto de selección vuelve a comprobar el desplazamiento al soltar el ratón. El código de crecimiento y del rayo conserva la misma separación de componentes y el orden de oclusión.

## Revit 2027.2

Con el complemento de referencia, el ejecutor verificó en RFA el cubo, la silla, el zapato, el florero y el frasco. Comprobó las identidades B2, los RGB efectivos, las etiquetas de ensayo con identidad coincidente y los materiales de plantilla. Tras cargar y colocar las cinco familias, guardó y reabrió el RVT y verificó dimensiones, posición y referencias a los RGB propios en cada instancia. Se usaron 500 mm para el cubo y una altura de ensayo de 750 mm para la silla y los objetos Meshy. También repitió el cubo nativo, la familia paramétrica 3A y la importación completa. [Resultados API de referencia](../artifacts/stage5d-c2b-20261123/api-205133/results.json) · [RVT de referencia](../artifacts/stage5d-c2b-20261123/api-205133/C2B_Multimodel.rvt)

Con la compilación corregida, los siete RFA se generaron, guardaron y reabrieron. Las siete familias se colocaron en un único RVT, que se guardó y reabrió. Los materiales RGB siguieron asociados a la geometría de cada instancia, con nombres independientes y sin alteración de los materiales de la plantilla de familia. Se probó el uso de etiquetas por identidad completa en el importador, incluida la exclusión de una identidad con un hash de pieza incorrecto. [Resultados API corregidos](../artifacts/stage5d-c2b-20261123/api-205617/results.json) · [RVT multióbjeto](../artifacts/stage5d-c2b-20261123/api-205617/C2B_Multimodel.rvt)

| Modelo | RFA, importaciones verificadas | Tiempo acumulado hasta reabrir y verificar | RVT tras reabrir |
|---|---:|---:|---|
| Frasco | 2 | 3.24 s | Geometría, posición y RGB verificados |
| Zapato | 29 | 4.18 s | Geometría, posición y RGB verificados |
| Florero | 9 | 3.70 s | Geometría, posición y RGB verificados |
| Máquina 1 | 148 | 10.39 s | Geometría, posición y RGB verificados |
| Máquina 2 | 134 | 7.47 s | Geometría, posición y RGB verificados |
| Silla | 6 | 3.05 s | Geometría, posición y RGB verificados |
| Cubo | 1 | 8.41 s, primera importación de la sesión | Geometría, posición y RGB verificados |

## Pruebas gráficas en el cuarto comando

Se abrió cada modelo Meshy y la silla en el editor del complemento compilado. Los planes siguientes se guardaron con caras elegidas haciendo clic en la malla visible. El ejecutor Core verificó la derivación de estos planes contra sus OBJ. [Proyecciones de los planes guardados](../artifacts/stage5d-c2b-20261123/planes-visuales.png) · [Verificación geométrica](../artifacts/stage5d-c2b-20261123/visual-geometry.log)

| Modelo | Resultado gráfico y límites | Evidencia |
|---|---|---|
| Silla | El cojín de `component_001` creció a 565 caras con 25° y quedó en RGB `0,80,220`. Se vio la estructura debajo en gris `150,150,150`. Después de girar y acercar, se colorearon 379 caras visibles del respaldo de `component_003` en `169,64,39`. Se cambió de componente, se ocultó y recuperó la pieza activa, se guardó y recargó el plan, y se canceló un borrador temporal. | [Plan](../artifacts/stage5d-c2b-20261123/chair/plan-visual.json) · [RFA de la interfaz](../artifacts/stage5d-c2b-20261123/chair/C2B_UI_Silla.rfa) |
| Florero | Girar hacia arriba y acercar permitió seleccionar la pared realmente visible. El crecimiento a 25° coloreó 688 caras de pared interior y borde en `0,80,220`. La superficie exterior de la misma pieza recibió `170,117,62`. El fondo aparente y los salientes grises siguen visibles y forman parte del OBJ. | [Plan](../artifacts/stage5d-c2b-20261123/meshy-2/plan-visual.json) |
| Máquina 1 | Una cara del panel elevado identificó `component_015`. La región creció a tres caras planas y quedó en `0,80,220` con respaldo gris. El panel quedó distinguible de la estructura y sus soportes. La interfaz creó y reabrió el RFA con 148 importaciones. | [Plan](../artifacts/stage5d-c2b-20261123/meshy-4/plan-visual.json) · [RFA de la interfaz](../artifacts/stage5d-c2b-20261123/meshy-4/C2B_UI_Maquina1.rfa) |
| Máquina 2 | La superficie inclinada superior de la carcasa se seleccionó en `component_001`. Creció a dos caras, en `27,152,116` con respaldo gris. Se distinguen el panel elevado y los elementos pequeños que ocluyen parte de esa superficie. | [Plan](../artifacts/stage5d-c2b-20261123/meshy-5/plan-visual.json) |
| Zapato | La cámara giró por debajo para elegir una cara de la suela. A 25° el resaltado pasó de la suela al cuerpo con 1059 caras. Se deshizo el crecimiento y a 12° se aisló un parche inferior de 102 caras, en `0,80,220` frente a respaldo gris. El cruce a 25° se explica por la propagación entre caras contiguas cuyo ángulo local es compatible, sin un límite que represente la suela entera. | [Plan](../artifacts/stage5d-c2b-20261123/meshy-1/plan-visual.json) |
| Frasco | El respaldo `191,120,38` se aplicó a la pieza del cuerpo sin crear una región. Crecer a 25° seleccionó 1884 caras de la tapa en RGB `0,80,220`. El crecimiento se detuvo en la junta. Al ocultar la tapa pudo verse el cuello hueco del cuerpo. | [Plan](../artifacts/stage5d-c2b-20261123/meshy-3/plan-visual.json) |
| Cubo | Esta ejecución repitió Core y Revit API. La interacción gráfica del cubo quedó registrada en la sesión de referencia. | [Prueba visual de referencia](stage5d-c2b-validation.md) |

El ensayo en RVT cargó copias bit a bit de los RFA de la silla y la Máquina 1 generados desde la interfaz, junto a los siete RFA de API. Las nueve instancias conservaron tamaño, posición y colores efectivos tras reabrir. Se verificaron además las regiones y sus hashes contra los dos planes visuales en los RFA, antes de cargar el proyecto. También pasaron el cubo nativo, 3A, OBJ completo y 5A. [Resultados con familias de interfaz](../artifacts/stage5d-c2b-20261123/api-211350/results.json) · [RVT de nueve familias](../artifacts/stage5d-c2b-20261123/api-211350/C2B_ConInterfaz.rvt)

Con `C2B_API_chair.rfa` activo en Revit, se volvió a abrir el OBJ de silla, se cargó el plan visual desde cero y se guardó una segunda versión con el nombre «Cojín azul» y la misma selección y RGB. Tras generar y reabrir `C2B_UI_SillaC1.rfa`, **Consultar etiquetas existentes** mostró «Etiqueta C1 de ensayo» para `component_006`. [Plan visual revisado](../artifacts/stage5d-c2b-20261123/chair/plan-visual-v2.json) · [RFA con etiqueta transferida](../artifacts/stage5d-c2b-20261123/chair/C2B_UI_SillaC1.rfa)

La primera repetición API de 5C abrió el OBJ mediante una ruta con segmentos `..` y el ejecutor no recibió los dos materiales previstos. La repetición por el flujo normal, con una copia de bytes idénticos de OBJ y MTL en la misma carpeta, detectó dos materiales explícitos. Se aplicaron `20,140,230` a Green y `197,68,133` a Red. La creación normal guardó el RFA solo tras verificar ambos materiales en la reapertura. Se retiró después el ejecutor temporal y se comprobó el hash del manifiesto de instalación restaurado. [RFA de la regresión 5C](../artifacts/stage5d-c2b-20261123/regression/C2B_UI_Materiales5C.rfa)

## Aceptación

La batería de siete modelos y las comprobaciones visuales esenciales de 5D-C2B quedaron aprobadas. El alcance cubre el OBJ derivado con correspondencia íntegra de caras y, en Revit, la presencia de la geometría, el ensamblaje, el tamaño, la posición y los materiales por instancia. La validación por cara se realizó contra los archivos OBJ y sus manifiestos. El gesto físico Ctrl+clic se cubrió mediante la lógica de agregar y retirar caras en Core. El cubo tiene evidencia gráfica de referencia y repetición Core/API en esta sesión. La selección angular orgánica sigue requiriendo revisar el resaltado, como muestra el zapato. La ampliación del límite se comprobó hasta 148 componentes con los equipos disponibles.

Con esa cobertura se puede avanzar a 5D-C3. Conviene conservar estos siete casos como batería de regresión. La etapa futura **5F — Optimización automática de polígonos según la fotografía** permanece en la hoja de ruta.
