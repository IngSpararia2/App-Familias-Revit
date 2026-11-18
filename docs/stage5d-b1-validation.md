# Etapa 5D-B1 — extracción local de componentes OBJ

## Estado y alcance

Al iniciar: rama `main`, árbol limpio, commit `9fe58a3` («Etapa 5C», cuyo contenido incluye 5D-A). Se revisaron los informes 5C y 5D-A, `ObjStructure`, `ObjInspection`, comprobaciones Core, contrato del importador y copia local de la silla Meshy T2. El OBJ de silla de 5D-A seguía disponible tanto en caché como en `artifacts/stage5probe/chair.obj` (copia ignorada). No se editaron esos archivos ni las funciones de Revit, Meshy, WPF o instalación.

`ObjComponentExtractor` reside en Core, sin Autodesk, red ni paquetes nuevos. Usa `ObjStructure.AnalyzeWithFaces` para obtener la misma numeración estable de componentes (orden por menor índice de posición utilizado). Una pasada propia lee exclusivamente los datos necesarios para extraer: líneas `v`, `vt`, `vn`, caras y estados `o`, `g`, `usemtl`, `s`, además de `mtllib`. No repite el algoritmo de conectividad. Rechaza otras directivas porque no puede garantizar que sus efectos se conserven. `ObjInspection` mantiene los límites y validación base de 3B/5D-A.

Uso local explícito, fuera de Revit:

```powershell
dotnet run --project tests/BimFamilyAI.Core.Checks -c Release -- --extract <origen.obj> <carpeta-nueva>
```

La carpeta de destino no puede existir. Se crean archivos `component_001.obj`, `component_002.obj`, etc., y `manifest.json`. La numeración coincide con `ObjStructure.Components`. La aplicación escribe primero en una carpeta temporal hermana, valida todas las piezas, hashes y manifiesto y mueve la carpeta al destino solo al terminar. Ante error elimina la carpeta temporal y no sobrescribe una extracción previa. No hay operación de red ni generación IA.

## Conservación y verificación

Cada pieza recibe solo los vértices de posición, UV y normales referenciados por sus caras, en el orden original de declaración; las líneas numéricas se copian sin recalcular ni redondear sus valores. Se convierten índices positivos, negativos y adelantados a índices positivos locales. Se preservan las cuatro formas de esquina `v`, `v/vt`, `v//vn` y `v/vt/vn`, el orden de esquinas, el número de lados de polígonos y los estados efectivos `o`, `g`, `usemtl` y `s`. La sintaxis y referencias `vt`/`vn` se validan antes de emitir. Se admite UTF-8 válido; los formatos o directivas que no pueden conservarse se rechazan.

La comprobación interna vuelve a leer cada OBJ generado y compara, por número de cara original, **cada índice de posición/UV/normal resuelto y la forma de esquina**, además de cada línea `v`/`vt`/`vn` que le corresponde. Comprueba caras y triángulos nominales, una sola componente topológica por archivo, caja individual y suma global de caras. El manifiesto enumera cada cara original exactamente una vez; la unión de cajas de piezas debe coincidir exactamente con la caja de la malla de origen. Se comprueban SHA256 del origen antes y después, y de cada archivo generado. Esta validación incluye orientación por conservación del orden de esquinas; no depende de una comparación visual ni solo de cajas. Cambian necesariamente la numeración de índices y el espaciado de líneas, **no** las coordenadas numéricas, relaciones de caras o asociaciones de atributos.

`manifest.json` (versión 1) guarda SHA256 del origen, número y nombres de piezas, caras/triángulos/vértices por pieza, caja y centro individual y global, números de caras y vértices originales (base 1), hashes de los OBJ/MTL y advertencias. No guarda rutas absolutas, credenciales, datos de imagen ni marcas de tiempo. Declara que unidad física y eje vertical no están determinados por el OBJ; el usuario deberá confirmarlos en el flujo Revit futuro. Un mismo origen y perfil de extracción generan los mismos bytes OBJ y manifiesto.

### Materiales y archivos auxiliares

Cuando hay `usemtl` en caras, cada `mtllib` debe nombrar un archivo **local de nombre simple** en la misma carpeta del OBJ; se rechazan rutas, enlaces, MTL ausentes y materiales no definidos. Se copia el MTL byte por byte tras comprobar SHA256. Por ahora se permiten declaraciones básicas de material; mapas de textura u otras directivas con auxiliares se rechazan para no publicar referencias rotas. Si `mtllib` está presente pero ninguna cara usa `usemtl`, se omite de las piezas y se advierte si el archivo falta. No se inventan materiales ni texturas; apariencia Realista permanece fuera de alcance.

## Resultados reproducibles

| Origen | Componentes publicados | Caras conservadas | Resultado relevante |
|---|---:|---:|---|
| `cube_500mm.obj` | 1 | 12/12 | Caja (0,0,0)–(500,500,500); repetición byte a byte idéntica. |
| `offset_prism_m.obj` | 1 | 12/12 | Centro (10.5,20.3,30.375) sin recentrado. |
| `material_regions.obj` | 1 | 12/12 | Grupos con vértices compartidos permanecen juntos; MTL local copiado exactamente. |
| `shared_material.obj` | 1 | 12/12 | Dos grupos y un material no producen piezas ficticias. |
| Sintético con dos componentes, cajas idénticas, UV/normales, polígono y caras triangulares | 2 | 3/3 | No fusiona por proximidad ni por coordenadas duplicadas; conserva índices/atributos/estados. |
| Silla real Meshy T2 local | **6** | **4172/4172** | 2195 vértices de posición y 4152 normales distribuidos; sin UV ni `usemtl`. |

Silla real, orden estable de `ObjStructure`:

| Pieza | Vértices | Caras | Normales | Caja mínima | Caja máxima |
|---|---:|---:|---:|---|---|
| 001 | 1106 | 2154 | 2138 | (-0.252393,-0.500000,-0.213749) | (0.249792,-0.073672,0.306731) |
| 002 | 80 | 131 | 131 | (-0.190251,-0.106630,-0.182478) | (-0.121114,-0.087385,-0.127130) |
| 003 | 744 | 1445 | 1441 | (-0.270561,-0.150838,-0.305904) | (0.267692,0.500000,-0.119151) |
| 004 | 93 | 156 | 156 | (0.120632,-0.121740,-0.179470) | (0.185742,-0.099196,-0.123586) |
| 005 | 82 | 135 | 135 | (0.118121,-0.108436,-0.182461) | (0.188517,-0.091198,-0.123737) |
| 006 | 90 | 151 | 151 | (-0.186678,-0.118234,-0.179651) | (-0.124754,-0.096449,-0.125043) |

La caja global de la silla es (-0.270561,-0.500000,-0.305904)–(0.267692,0.500000,0.306731), en coordenadas OBJ sin unidad física confirmada. El manifiesto contiene los números 1–4172 exactamente una vez. La extracción se repitió en carpetas ignoradas; hashes de las seis piezas y del manifiesto idénticos. SHA256 del OBJ original antes/después idéntico. Once pares de cajas de componentes se superponen según 5D-A; esto no prueba intersección ni identidad semántica. `mtllib model.mtl` señala un archivo ausente, pero ninguna cara usa `usemtl`: se omite de la salida y queda advertencia explícita. La salida final comprobada está en `artifacts/stage5d-b1-chair-final/` (ignorada por Git).

## Pruebas y rechazos

- Core.Checks: **103/103**. Incluye los 41 controles previos y extracción de malla única, dos componentes, material compartido, grupos distintos/conectados, cajas superpuestas, coordenadas duplicadas, triángulos, polígonos, índices negativos y adelantados, UV/normales, suavizado, MTL local y repetición determinista.
- Rechazos verificados: MTL requerido ausente, material no definido, índices `vt`/`vn` fuera de rango, ruta MTL insegura, mapa de textura no soportado, directiva geométrica desconocida, UTF-8 inválido, OBJ vacío/sobredimensionado, pieza individual degenerada, destino existente y fallo de escritura. En el caso de pieza inválida después de empezar a escribir se eliminó el temporal y no apareció una salida parcial.
- Meshy.Checks: **187/187**, manejadores HTTP simulados; **0 POST de producción y 0 créditos consumidos** en esta etapa.
- Solución Release/x64: **0 errores, 2 advertencias MSB3277 preexistentes** de referencias Autodesk. El proyecto de comprobaciones de materiales Revit también compiló; no se ejecutó Revit en esta etapa.
- `git diff --check` sin errores. Solo Core, comprobaciones y documentación cambiaron; sin commit ni push.

## Límite de 5D-B1 y contrato para 5D-B2

La extracción demuestra separación topológica, no nombres de piezas, no cortes de mallas conectadas y no edición independiente en RFA. Una componente individual cuya geometría no cumple `ObjInspection` se rechaza junto con toda la extracción. Texturas/materiales complejos y directivas OBJ ajenas al subconjunto validado requieren una ampliación probada. No se verificaron intersecciones entre superficies de piezas ni cierre de cada malla.

En 5D-B2, importar cada OBJ con el `ObjFamilyImporter` actual sería incorrecto: cada llamada calcula su escala desde **la altura de esa pieza** y después centra esa pieza en X/Y y apoya su mínimo Z en cero. Eso destruiría las posiciones relativas. La nueva ruta deberá calcular una **sola escala, rotación de eje y traslación** desde la caja global del origen y la altura/unidad confirmadas, y aplicarlas igual a todas las importaciones sobre el mismo sistema de coordenadas. `ObjFamilyImporter` necesitará una operación de importación de pieza sin centrado/base individuales y una validación agregada de todas las `ImportInstance`; `FamilyFileWorkflow` tendrá que guardar/reabrir y verificar más de una importación; `ImportedMaterials` necesitará inspección/asignación por pieza sin editar materiales de plantilla ni confundir material compartido con componente. Esos cambios y la prueba real dentro de Revit pertenecen exclusivamente a 5D-B2.
