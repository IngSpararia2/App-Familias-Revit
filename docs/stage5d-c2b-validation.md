# Etapa 5D-C2B · Editor visual de regiones

La implementación parte de `main` en `7aa8e76dc2dc3791ec15d3af278245db5c69f928` con el árbol limpio.

## Uso y conservación

En Preparación BIM, seleccione un OBJ local o recupere uno existente y confirme su eje vertical de origen. Abra el editor visual y seleccione la componente activa. Arrastre con el botón derecho para girar, use la rueda para acercar y pulse **Reencuadrar** para recuperar la cámara inicial. El clic selecciona la cara visible más cercana. Ctrl + clic alterna caras. Puede ocultar componentes temporalmente para acceder a superficies tapadas.

**Crecer** recorre aristas compartidas según el umbral angular y se detiene ante bordes ambiguos o caras de otra región. **Deshacer selección** restaura el conjunto anterior. Aplique nombre, RGB y caras para actualizar una región. Seleccione una región de la lista para editarla o eliminarla. Las caras sin región conservan su material original cuando lo tienen. El RGB de respaldo se aplica a las caras sin material explícito.

Guardar crea un JSON nuevo compatible con 5D-C2A. Cargar valida el SHA256 y los IDs originales contra la extracción. **Usar regiones** transfiere el plan confirmado a Preparación BIM. Después, confirme altura y nombre y cree el RFA. Cancelar descarta los cambios del editor en memoria. Los planes que el usuario haya guardado explícitamente quedan disponibles.

La vista previa triangula polígonos planos mediante recorte de orejas y conserva en cada triángulo la correspondencia de la pieza B1 con la cara original. Un polígono degenerado, no plano o autointersectado se rechaza con su número de cara. El rayo usa la misma proyección ortográfica que WPF y elige la intersección más cercana entre componentes visibles. La cámara utiliza la orientación de `ObjAssemblyTransform` y solo modifica la presentación. [Viewport3D en WPF](https://learn.microsoft.com/en-us/dotnet/api/system.windows.controls.viewport3d?view=windowsdesktop-10.0)

El RFA usa el OBJ/MTL derivado por 5D-C2A y la transformación global 5D-B2. La geometría que se genera proviene de la extracción verificada. Las triangulaciones de la vista previa no se exportan. El ensamblador comprueba el RFA antes y después de guardar. Si el RFA activo contiene etiquetas 5D-C1 con el mismo SHA256 de origen, componente y pieza, la nueva familia las recibe. El RFA existente queda disponible como referencia.

## Comprobaciones ejecutadas

- Release/x64 de la solución y del ejecutor Revit. Cero errores y dos advertencias MSB3277 preexistentes.
- Core 162/162. Incluye oclusión, orientación, selección múltiple, concavidad, índices negativos, crecimiento, conflictos, persistencia y validación geométrica C2A. La silla local mostró una correspondencia de 4172 caras originales y seis componentes.
- Meshy 187/187 con transporte simulado y cero POST reales.
- Revit 2027.2, 25/25. Los rayos del editor seleccionaron las dos caras de la tapa del cubo. El ejecutor guardó y cargó el plan, derivó mediante C2A y generó dos RFA. Verificó una sola importación, RGB independientes, geometría, identidad y etiqueta manual tras reabrir. Cargó ambas familias en un RVT y comprobó su independencia después de reabrir. También generó la silla de seis importaciones con regiones técnicas, y repitió el cubo nativo, el flujo 3A, la importación simple, la lectura de etiquetas y la edición de materiales 5C. [Resultados API](../artifacts/stage5d-c2b-20261119-185027/results.json)

En Computer Use se ejecutó el cuarto comando, se abrió el cubo local y se seleccionaron dos zonas haciendo clic y creciendo con el umbral de 25°. El plan guardó **Tapa** azul en las caras originales 3 y 4, **Lateral** rojo en las caras 5 y 6 y respaldo gris. El editor recuperó el JSON y la interfaz normal generó y abrió `SIM_Familia_Foto_6.rfa`, que mostró las tres áreas en Sombreado. El RVT se verificó por API con las dos familias del ejecutor. [Plan visual](../artifacts/stage5d-c2b-20261119-185027/plan-cubo-ui.json) · [Editor](../artifacts/stage5d-c2b-20261119-185027/editor-cubo.png) · [RFA sombreado](../artifacts/stage5d-c2b-20261119-185027/rfa-cubo-sombreado.png)

## Límites

La selección es manual. Una región es una asignación por caras y permanece dentro de su importación Revit. La vista de materiales es gráfica. Después de la prueba del cubo se ajustaron la iluminación, las normales de vista previa y el gesto de arrastre. Esa revisión compiló y se instaló, y se volvió a abrir el flujo de Revit. La visualización de la silla en el editor, el giro y el zoom de la revisión final quedaron pendientes al cerrar la sesión de validación. La silla sí pasó por Core y por la generación API con una selección técnica. La selección visual del cojín y la carga en RVT del RFA exacto creado por la interfaz quedan pendientes. La etapa futura **5F — Optimización automática de polígonos según la fotografía** sigue pendiente.

## Validación multióbjeto del 23 de noviembre de 2026

Se partió de `main`, HEAD `32e3e12a5356f047dab3d23726c775ace6049270`, alineado con `origin/main` y con el árbol limpio. Se encontraron siete OBJ en el almacén Meshy Real, cinco de la sesión del 23/11 y dos anteriores. Las cinco descargas recientes estaban validadas y sus archivos coincidieron con los SHA256 del historial. Se trabajó con copias, sin modificar el historial ni los OBJ originales. [Matriz, diagnósticos y evidencias](stage5d-c2b-multimodel.md)

La validación Core cubrió los siete casos. La suite Release/x64 pasó 165 comprobaciones de Core y 187 de Meshy con transporte simulado. La compilación Release/x64 terminó con cero errores y dos advertencias MSB3277 de Autodesk.

Revit 2027.2 generó y reabrió los siete RFA por API. Computer Use confirmó la selección gráfica en las seis mallas Meshy y generó dos RFA desde la interfaz normal. El RVT de prueba reabrió con las nueve familias, incluidas las copias exactas de esos dos RFA. Se verificó la transferencia 5D-C1 al repetir la generación de la silla desde su familia activa. [Resultados y alcance de aceptación](stage5d-c2b-multimodel.md)
