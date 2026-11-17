# Etapa 5C — materiales gráficos múltiples independientes

## Alcance y arquitectura

`ImportedMaterials` agrupa las referencias de malla/cara por `MaterialId` efectivo. Dos grupos OBJ no implican dos materiales editables: si la referencia es inválida o ambos grupos apuntan al mismo ID, no se ofrece una separación ficticia. El diálogo conserva las tres opciones 5A y añade **Personalizar materiales por región** únicamente al detectar al menos dos materiales explícitos independientes y propios de la nueva importación. Muestra nombres originales, número de referencias, RGB y una muestra; solo los materiales marcados reciben nuevo RGB. Los otros conservan color, transparencia y opción de sombreado. RGB fuera de 0–255 impide continuar.

`ApplyMultiple` comprueba IDs/nombres actuales y rechaza referencias de plantilla, duplicados y ausencia de materiales independientes. En modo múltiple asigna nombres únicos a todos los materiales propios de la importación, incluidos los no recoloreados. Esto conserva su apariencia gráfica y evita la fusión por nombre entre familias cargadas en un mismo RVT, una regresión detectada en la primera corrida de integración. No renombra ni modifica materiales de plantilla. La edición ocurre en transacción. `FamilyFileWorkflow` conserva el guardado temporal, reapertura y publicación sin sobrescribir; valida geometría/dimensiones con el importador existente y compara tras reabrir nombres, RGB, transparencia, sombreado y número de referencias. Los `ElementId` solo viven durante la operación abierta: los nombres únicos sirven para comprobar la persistencia.

No se modificaron Core, Meshy, ObjFamilyImporter, UniformObjMaterial, geometría, escala, orientación, GUID ni instalador. No hay texturas, apariencia Realista garantizada, parámetros Material por componente ni identificación automática de asiento/patas/respaldo. La opción RGB uniforme 5A conserva su copia temporal OBJ/MTL original; la modalidad múltiple no la usa.

## Archivos

- Modificados: `src/BimFamilyAI.Revit/ImportedMaterials.cs`, `ImportedMaterialDialog.cs`, `FamilyFileWorkflow.cs`, `PhotoFamilyWindow.cs`, `tests/BimFamilyAI.Revit.MaterialChecks/MaterialChecks.cs`, `README.md`, `docs/architecture.md`.
- Creados: `tests/fixtures/shared_material.obj` y este informe.

## Validación real (16/11/2026)

La solución y el ejecutor de integración compilaron en Release/x64 con **0 errores y 2 advertencias MSB3277 preexistentes** cada uno. Core.Checks: **11/11**; Meshy.Checks: **187/187**, manejadores HTTP simulados y POST de producción **0**. No se consumieron créditos Meshy.

El ejecutor temporal en Revit 2027.2 terminó con **77 comprobaciones aprobadas y 0 errores**: `artifacts/stage5c-20261116-224701/results.json`. El fixture de dos materiales `material_regions.obj` produjo rojo `240,20,20` y azul `0,40,255` simultáneos. Al editar solo uno, el otro conservó `255,0,0`. Se guardaron y reabrieron RFA; las firmas geométricas, dimensiones, referencias y RGB persistieron. Se cargaron ocho familias en un RVT propio, se verificaron colores de instancias colocadas, se guardó/reabrió el proyecto y se repitió la verificación. Cubo sin MTL, conservar original, RGB uniforme, silla y los flujos nativo, paramétrico, OBJ manual y contrato fotográfico simulado continuaron pasando. El RFA de diálogo `Stage5C_Dialog.rfa` también se guardó/reabrió y validó.

Después de instalar la DLL normal, se comprobaron las cuatro entradas de Herramientas externas y se abrió el cuarto comando. En modo simulado se recuperó un OBJ ya descargado con contador **POST=0, GET=0, descargas=0 y créditos=0**; se creó un RFA adicional `SIM_Familia_Foto_5.rfa` con altura 750 mm, eje Z y RGB uniforme `240,20,20`, sin generar otro modelo. El OBJ sin materiales explícitos no mostró la cuarta opción, como corresponde.

En el diálogo WPF real, Cancelar devolvió ninguna selección; el RGB `999,0,0` mostró error y dejó el diálogo abierto; luego se marcaron dos materiales con rojo y azul válidos y se guardó el RFA. El fixture `shared_material.obj` contiene dos grupos con el mismo `usemtl Red`; Revit dejó sus referencias sin `MaterialId` editable en esta prueba. El ejecutor comprobó que no se ofrece separación múltiple y que `ApplyMultiple` la rechaza. La prueba de protección de material de plantilla intentó una asignación con su ID y comprobó que no cambió el RGB.

Resultados fallidos conservados para diagnóstico: la primera ejecución del ejecutor calculó mal la raíz al usar `bin/x64` y no encontró fixtures; se corrigió con búsqueda del archivo de solución. Una corrida posterior detectó fusión de materiales homónimos al cargar familias en un RVT; se corrigió aislando nombres. Un ensayo visual creó una importación nueva al final de la sesión sin referencias materiales explícitas, por lo que el diálogo ocultó correctamente la cuarta opción; para probarla se abrió una familia de dos materiales que la misma corrida ya había validado. La última corrida aprobada es la indicada arriba. El manifiesto temporal `BFAI_Stage5Checks.addin` se retiró y el complemento normal se reinstaló.

## Reproducción y pendientes

Para reproducir: compile el ejecutor de integración Release/x64, registre temporalmente su `IExternalApplication`, arranque Revit con los fixtures y plantilla métrica indicados en el código, y abra `results.json` y el RVT de la carpeta `artifacts/stage5c-*`. Retire el manifiesto temporal después. En uso normal, recupere un OBJ ya existente, confirme altura y eje, marque **Materiales y apariencia**, pulse Crear RFA y elija la modalidad múltiple si aparece. No pulse Generar para recolorear. En Sombreado, abra el nuevo RFA o cárguelo junto a otros en un RVT para comparar colores.

Pendiente de 5D: identificación semántica de componentes donde el OBJ no proporciona materiales separables. Pendiente de 5E: MTL/texturas y apariencia Realista. El cuarto comando conserva su flujo simulado y las cuatro entradas Ribbon originales; no se realizó una nueva generación Meshy para esta etapa. No hubo commit ni push.
