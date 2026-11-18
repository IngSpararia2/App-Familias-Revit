# Etapa 5D-C1 — identificación y etiquetas manuales

## Alcance y punto de partida

Se partió de `main` en `3af95f287b970ce766daf2522ec21355994ff56c`, con el árbol limpio. Se reutilizó la silla local `artifacts/stage5probe/chair.obj` y una **copia** del RFA creado en 5D-B2; no se modifican los OBJ ni las familias originales. La silla tiene seis componentes topológicos, sin nombres semánticos deducibles de sus índices. `shared_material.obj` es solo un fixture distinto.

## Identidad, almacenamiento y operación

La identidad de 5D-B2 permanece en el esquema `45ca17db-d2e5-4c44-9981-2c824f842dd3`: `ComponentId`, `SourceSha256` y `PieceSha256`. `ObjAssemblyImporter.TryReadIdentity` lee esos campos sin cambiarlos. La etiqueta manual se guarda en **otro** esquema, `BFAI_ComponentLabel_v1` (`b85ae122-22ec-49d0-936f-b73f3e630a7e`), con `ComponentId`, `Name` y `Version=1`. El `ComponentId` permite detectar etiquetas incongruentes. Quitar el nombre elimina únicamente la entidad del segundo esquema. No se usa Comentarios como fuente de verdad.

En el editor de una familia por componentes, abra **Complementos → Herramientas externas → BIM Family AI — Crear familia desde fotografía**. Seleccione visualmente una importación, pulse **Consultar selección**, compruebe el `component_###` mostrado y escriba el nombre. **Asignar/cambiar** y **Quitar etiqueta** actúan sobre esa selección; **Consultar etiquetas existentes** lista los nombres del documento activo. Los nombres son decisiones del usuario. Se admiten nombres repetidos; se rechazan vacíos, caracteres de control y más de 120 caracteres. El número de caras no se presenta como dato del RFA porque la importación nativa de Revit no conserva de forma fiable las caras del OBJ para esa lectura.

La ventana WPF no toca el documento: envía una solicitud inmutable al `PhotoFamilyExternalEvent` ya existente. `ComponentLabels` verifica familia Modelo genérico, selección única, importación embebida e identidad 5D-B2. La escritura usa una transacción de Revit y comprueba que la identidad y la etiqueta resultantes sean las esperadas. La solicitud de escritura lleva `UniqueId` y `ComponentId` leídos previamente; si cambió la selección, se rechaza para evitar etiquetar otra pieza. La aplicación no guarda automáticamente el archivo: el usuario guarda el RFA mediante Revit. Un proyecto RVT, selección vacía/múltiple/ajena, identidad ausente o documento de solo lectura se rechazan con un mensaje.

Las familias anteriores de 5D-B2 no necesitan regeneración ni una tarea Meshy nueva: la primera escritura añade el esquema complementario a la importación ya existente. El esquema original, hashes, geometría, transformación, materiales y orden de importación permanecen intactos. En un RVT cargado la familia sigue siendo funcional, pero la edición de etiquetas requiere abrir su editor de familias; no se promete acceso directo a las entidades de cada importación desde una instancia colocada en el proyecto.

## Validación

| Comprobación | Resultado |
|---|---|
| Solución Release/x64, .NET 10 y Revit 2027.2 | Compiló con 0 errores; 2 advertencias MSB3277 preexistentes de dependencias Autodesk. |
| Core.Checks | 113/113. |
| Meshy.Checks | 187/187; transporte HTTP simulado, 0 POST reales. |
| Prueba API Revit de etiquetas | **44/44**, sin errores. `artifacts/stage5d-c1-20261118-001934/results.json`. |
| Computer Use en Revit 2027.2 | Ejecutada: selección visual de respaldo y estructura, consulta de ID, cambio de etiqueta, guardado y reapertura del RFA, colores conservados. |

La prueba API creó una copia del RFA original de 5D-B2 y comprobó las seis identidades, selección válida/vacía/múltiple/ajena, protección frente a selección cambiada, asignación, cambio, eliminación, nombres duplicados, valores inválidos, rechazo de un RFA simple anterior y de un RVT. Comparó identidad, caja y materiales de cada importación antes y después de guardar y abrir otra copia en segundo plano. Las seis importaciones permanecieron. El RFA original no se modificó. Los dos primeros intentos del ejecutor revelaron que Revit no permite cerrar el documento activo ni cambiarlo dentro de `Idling`; se corrigió la prueba sin cambiar el código de producción y la última ejecución terminó 44/44.

Con Computer Use se abrió la copia `artifacts/stage5d-c1-20261118-000937/Stage5D_C1_Chair_Labeled.rfa` en Revit 2027.2. En la vista 3D Sombreado, al seleccionar el respaldo negro Revit resaltó una importación y la ventana leyó `component_003 → Respaldo`. Se cambió manualmente a **Respaldo curvo**. Al seleccionar la región roja de estructura/asiento, la ventana leyó `component_001 → Estructura y asiento`. Se guardó, cerró y reabrió el RFA; **Consultar etiquetas existentes** mostró los mismos dos IDs y nombres, con seis componentes. La silla conservó la forma y los colores rojo/negro observables. Los cuatro comandos seguían visibles en Complementos. Capturas locales: `artifacts/stage5d-c1-20261118-000937/selected-back.jpg`, `selected-seat.jpg`, `chair-after-reopen.jpg` y `labels-after-reopen.jpg`.

No se volvió a cargar esta copia en un RVT durante 5D-C1. La carga y persistencia de geometría/materiales en RVT se habían probado en 5D-B2; aquí se verificó que el proyecto RVT no habilita etiquetado de importaciones internas. No se repitió una generación OBJ completa ni las baterías históricas de 3A/5A/5C dentro de Revit: sus rutas de producción permanecen sin cambios y la regresión pertinente de familia simple se comprobó en la prueba API. La prueba de solo lectura queda cubierta por la condición de rechazo del servicio, pero no se ejecutó con un RFA realmente abierto como solo lectura.

## Límites y 5D-C2

Una importación es una componente **topológica**, no necesariamente una pieza funcional: el cojín y la estructura inferior pueden estar conectados dentro de `component_001`. Etiquetar esa importación no separa caras ni crea otra geometría. La futura 5D-C2 necesitará una representación verificable de regiones de caras dentro de una componente y una estrategia de material por región sin alterar la conservación geométrica ni la identidad de 5D-B2. Esta etapa no implementa selección de triángulos, segmentación, reconocimiento automático, texturas ni sólidos nativos.
