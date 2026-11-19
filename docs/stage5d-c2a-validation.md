# Etapa 5D-C2A — materiales por regiones de caras

## Alcance y estado inicial

Se partió de `main`, commit `8d5de278d5a26bf82ec56ef20e3b3135ad3cefdf`, con el árbol limpio. La silla local `artifacts/stage5probe/chair.obj` está disponible: 4172 caras y seis componentes topológicos. El OBJ no contiene etiquetas fiables de cojín ni estructura. Esta etapa no ejecuta Meshy ni añade un editor gráfico de caras.

## Contrato y procedencia

`ObjRegionPlan` versión 1 contiene el SHA256 del OBJ original, una lista de componentes `component_###`, regiones con `RegionId`, nombre opcional, RGB y **números originales de cara**, más un color de respaldo opcional. Las caras no elegidas conservan su `usemtl` original cuando existe; las demás reciben el respaldo explícito. Se rechazan caras ajenas, fuera de rango, repetidas o reclamadas por dos regiones, conjuntos vacíos, IDs inválidos, origen cambiado y ausencia de respaldo cuando hace falta. Una región puede contener caras no contiguas. No implica una nueva componente topológica ni una etiqueta semántica.

`ObjComponentExtractor.DeriveRegions` verifica primero la extracción 5D-B1 y publica en una carpeta nueva un `plan.json` canónico, `manifest.json`, copias OBJ para las piezas elegidas y MTL RGB locales. Copia byte por byte los MTL originales necesarios. Conserva las líneas de geometría `v/vt/vn/f/o/g/s` de la pieza B1 y solo cambia directivas de material. Los nombres de materiales derivados incorporan una huella del plan; la publicación usa una carpeta temporal y no sobrescribe una extracción previa. Las referencias MTL inseguras, ausentes o con mapas no admitidos ya son rechazadas por la validación B1 reutilizada.

El manifiesto derivado distingue `SourceSha256`, `ExtractionManifestSha256`, `PlanSha256`, `OriginalPieceSha256`, `DerivedSha256` y hashes de MTL. El hash de la pieza geométrica 5D-B1 **no se sustituye** por el hash del OBJ recoloreado. `VerifyRegions` repite la verificación del origen y compara posiciones, UV, normales, caras, esquinas resueltas, orden, orientación, declaraciones de objeto/grupo/suavizado, material por cara y caja. Los manifiestos no contienen rutas absolutas privadas.

Ejemplo de plan para un OBJ cuya primera componente contiene las caras originales 1 y 2:

```json
{
  "FormatVersion": 1,
  "SourceSha256": "<sha256 hexadecimal del OBJ original>",
  "Components": [
    {
      "ComponentId": "component_001",
      "Regions": [
        {
          "RegionId": "azul",
          "Name": "Selección manual de prueba",
          "OriginalFaceNumbers": [1, 2],
          "Color": { "Red": 0, "Green": 40, "Blue": 255 }
        }
      ],
      "FallbackColor": { "Red": 90, "Green": 90, "Blue": 90 }
    }
  ]
}
```

La operación local se ejecuta con `dotnet run --project tests/BimFamilyAI.Core.Checks -c Release -- --regions <origen.obj> <extraccion-5D-B1> <plan.json> <carpeta-nueva>`. El destino no debe existir. Para obtener primero las piezas: `--extract <origen.obj> <carpeta-nueva>`. En la silla, los IDs originales de caras de `component_001` **no son consecutivos**; deben leerse del manifiesto B1, no suponerse como un intervalo continuo.

## Ruta Revit

`ObjAssemblyRequest.RegionDirectory` activa una ruta explícita del ensamblador existente. Cada pieza afectada se importa desde su OBJ derivado; las demás siguen usando su pieza B1. Se aplica la misma transformación global 5D-B2. El ensamblador exige materiales distintos expuestos dentro de la misma `ImportInstance`, protege materiales de plantilla, aísla los nombres con un GUID por familia y asigna RGB gráfico, sombreado y transparencia cero. Un esquema complementario `BFAI_FaceRegions_v1` guarda componente y hashes del plan/OBJ derivado. El esquema 5D-B2 mantiene el SHA256 de la pieza original y el esquema de etiquetas 5D-C1 no cambia. Los materiales son subreferencias gráficas: las regiones no se pueden seleccionar como elementos Revit individuales.

La documentación oficial confirma que Revit 2027 [admite importar OBJ](https://help.autodesk.com/cloudhelp/2027/ENU/Revit-Model/files/GUID-E8705303-0610-4A82-9118-0C3A742706D2.htm) y permite definir [color de sombreado de materiales](https://help.autodesk.com/cloudhelp/2027/ENU/Revit-Customize/files/GUID-8E88F590-4864-4C60-9433-4A70AC066605.htm). No garantiza cómo una importación concreta traduce varios `usemtl`; eso requiere la prueba API y visual en Revit instalado.

## Pruebas automáticas

Core.Checks Release/x64: **141/141**. Incluye un/tres materiales, respaldo, caras no contiguas, materiales preexistentes, dos componentes, UV/normales, índices negativos, polígonos, origen, MTL, plan y OBJ derivado alterados, determinismo, rechazo de solapamientos y publicación sin parciales. Meshy.Checks Release/x64: **187/187**, con transporte simulado y **0 POST reales**. Los fixtures originales siguen intactos. La batería Core también cubre las extracciones 5D-B1 anteriores.

La solución completa compiló en **Release/x64, 0 errores, 2 advertencias MSB3277 preexistentes** de referencias Autodesk. El ejecutor temporal de Revit compiló con el mismo resultado. No se cambiaron los cuatro comandos, sus GUID ni el instalador.

## Prueba API y visual en Revit 2027.2

El ejecutor local `FaceRegionChecks` terminó **24/24** en `artifacts/stage5d-c2a-20261119-181910/results.json`. En un cubo conectado de 12 caras creó dos RFA: tapa azul/cuerpo gris y tapa roja/cuerpo verde. El primer RFA contiene **una sola `ImportInstance` embebida con dos materiales efectivos**. `ObjAssemblyImporter.ValidateSaved` comprobó geometría, caja, dimensiones, identidad B2, hashes de región, RGB, sombreado y transparencia después de guardar y reabrir. El segundo RFA tiene materiales con nombres aislados. Ambas familias se cargaron y colocaron en `Stage5D_C2A_Regions.rvt`; tras reabrir el proyecto, la API verificó los pares de colores independientes.

La silla local conservó sus **seis componentes y 4172 caras**. El hash SHA256 del OBJ de origen siguió siendo `4ced7c29db566d1f48876bd88646a41f1134e39dc3e1f660a40419297bee18be` y coincidió con el manifiesto B1. El plan de prueba eligió los primeros 40 IDs originales que el manifiesto asigna a `component_001`, divididos en dos regiones de 20 caras, con gris para las demás. La familia resultante mide 750 mm de alto bajo la misma transformación global Y→Z de 5D-B2. `component_001` expuso tres RGB en su importación, además de un color verde uniforme en `component_003`. La API verificó las seis identidades legibles por 5D-C1. Los parches azules y rojos son una **selección técnica**, no un cojín reconocido.

Regresiones ejecutadas en Revit: importación simple, cubo nativo de 500 mm, familia paramétrica 3A, lectura de las etiquetas del RFA 5D-C1 anterior, exposición y edición independiente de dos materiales del fixture 5C, y carga/reapertura RVT. La ruta 5D-B1 se volvió a ejecutar para cubo y silla. No se inició ningún seguimiento/generación Meshy; las 187 pruebas Meshy usaron handlers simulados. No se repitió una generación real ni se afirmará que la batería histórica completa de comandos se ejecutó manualmente en la interfaz.

Se repitió además el ejecutor histórico `MaterialChecks` con la DLL final: **77/77** en `artifacts/stage5c-20261119-182923/results.json`. Cubrió color RGB uniforme 5A, materiales explícitos e independientes 5C, conservación de geometría y materiales de plantilla, ocho familias RFA, carga y reapertura RVT, cancelación del diálogo sin cambios y dos asignaciones RGB desde el diálogo. El instalador local se ejecutó con Revit cerrado y solo el manifiesto principal `BIMFamilyAI.addin` permanece registrado; los manifiestos temporales de pruebas se retiraron.

Computer Use se utilizó realmente para abrir el RFA y el RVT y observar las vistas 3D en **Sombreado**. Se vieron tapa azul/cuerpo gris en el RFA, ambos pares de colores en el RVT, y la silla ensamblada con áreas técnicas azules/rojas pequeñas y respaldo verde. Capturas locales: `artifacts/stage5d-c2a-20261119-181910/cube-two-face-colors.png`, `rvt-two-families-colors.png` y `chair-technical-face-regions.png`. El primer arranque del ejecutor llegó a 10 comprobaciones y reveló que los números 1–40 no pertenecen todos a `component_001`; se corrigió la **prueba** para tomar los IDs del manifiesto y se repitió íntegra con 24/24. No fue necesario cambiar el importador por ese fallo de datos de prueba.

## Límites y 5D-C2B

La selección de caras de la silla para esta prueba es **técnica**: toma IDs de cara del manifiesto de `component_001`, sin identificar visualmente el cojín. La precisión de una región «cojín» exige selección gráfica y comprobación visual en 5D-C2B. Esa etapa puede leer y editar `plan.json`, ofrecer selección múltiple y crecimiento topológico, y previsualizar el RGB; necesitará conservar los números de cara originales y volver a verificar todos los hashes. No se añaden IA, cortes de malla, texturas Realistas, sólidos nativos ni tareas pagadas.
