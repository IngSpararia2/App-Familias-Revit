# Etapa 3A — validación

## Implementación

Se conserva GenerateTestFamilyCommand y se añade GenerateParametricFamilyCommand. Comparten exclusivamente selección de RFT, control del documento independiente, guardado sin sobrescritura y limpieza. El manifiesto generado registra ambos comandos con GUID diferentes en el mismo archivo por usuario.

ParametricBoxBuilder crea una única extrusión y cuatro parámetros de tipo: Ancho, Profundidad y Altura (SpecTypeId.Length, grupo Geometry), y Material (SpecTypeId.Reference.Material, grupo Materials). No crea parámetros de instancia. Dos parámetros auxiliares de longitud, BFAI_Semiancho y BFAI_Semiprofundidad, usan fórmulas Ancho / 2 y Profundidad / 2; aparecen en Restricciones y no deben editarse manualmente.

Cuatro planos exteriores se acotan respecto a dos planos centrales fijados en el origen. Las cotas se etiquetan con las semidimensiones; las cuatro líneas del perfil se alinean y bloquean con los planos exteriores. Así se conserva el centrado. Altura se asocia al final de extrusión y el inicio permanece en cero. Material se asocia a MATERIAL_ID_PARAM con comprobación de tipo, posibilidad de asociación y vínculo resultante.

Se generan Cubo_500 (500/500/500, material BFAI_Rojo) y Prisma_1000x600x750 (1000/600/750, BFAI_Azul). Ambos usan la misma extrusión.

## Validación incorporada al generador

Antes de guardar, cambia ambos tipos, modifica las dimensiones a 1400/800/950 y vuelve a los valores de entrega. Cada cambio regenera y comprueba caja geométrica, posición, material y presencia del material en el sólido. Tolerancia espacial: 1e-6 pies internos (aproximadamente 0.000305 mm). Los errores abortan la entrega.

Después de SaveAs, cierra y reabre el archivo temporal; comprueba asociaciones de altura/material, ambos tipos y geometría. Prueba otras dimensiones dentro de una transacción que revierte antes de cerrar. Solo después mueve el RFA al destino final sin sobrescribir. Estas verificaciones se ejecutan dentro de Revit al invocar el comando; no son pruebas ejecutadas por compilar.

## Resultados actuales

- SDK 10.0.404, .NET 10 Windows x64, referencias locales de Revit 2027.
- Compilación Release de solución: cero errores, dos advertencias MSB3277 ya presentes antes de estos cambios; no se ocultaron ni se añadieron dependencias.
- git diff --check sin errores de espacios.
- Instalación actualizada ejecutada dos veces con Revit cerrado. XML con dos comandos y dos GUID distintos, sin registros duplicados. Ambos comandos visibles en Herramientas externas de Revit 2027.2.
- Generación real ejecutada: `BIMFamilyAI_ParametricBox.rfa`, 458752 bytes. El mensaje de éxito se mostró después de superar todas las comprobaciones de dimensiones, centrado, base, tipos, material y reapertura incorporadas al generador.
- Se abrió el RFA en la interfaz de Revit. El editor Tipos de familia muestra los cuatro parámetros y las fórmulas auxiliares. Desde ese editor se cambiaron Ancho=1400, Profundidad=800 y Altura=950; al aceptar se actualizó la geometría y se inspeccionó el prisma en 3D, sin invocar el complemento. Después se deshicieron los cambios de prueba; el archivo original en disco conserva los valores de entrega.
- Pruebas A/B/D/E: superadas mediante Revit API durante la ejecución real, incluidas las comprobaciones después de cerrar y reabrir. El cambio manual de material en la interfaz no se realizó: se verificaron dos materiales mediante API y su aplicación al sólido.
- Prueba F: comando fijo ejecutado desde Proyecto1, creó `BIMFamilyAI_TestCube_500mm_2.rfa` con su comprobación dimensional superada.
- Prueba G parcial: se creó un proyecto nuevo de prueba Proyecto1 y se ejecutaron ambos comandos. La vista y su contenido permanecieron visualmente iguales y el proyecto siguió activo. El paramétrico creó `BIMFamilyAI_ParametricBox_1.rfa`, también de 458752 bytes, con todas las validaciones superadas. No se inspeccionó IsModified ni se comparó el estado de guardado de un proyecto previamente guardado; esa parte queda pendiente.
  Actualización: se guardó el proyecto propio como `BFAI_Stage3A_Validation_20261115.rvt` en Generated. Se volvieron a ejecutar ambos comandos, creando el cubo fijo `_3.rfa` y el paramétrico `_2.rfa`. Al cerrar el proyecto mediante Ctrl+W, Revit lo cerró directamente sin solicitar guardar cambios y regresó a la familia abierta: verificado que la ejecución no dejó cambios pendientes en el proyecto guardado. No se leyó IsModified mediante API; la comprobación fue mediante la interfaz. No se ensayó un proyecto con modificaciones previas sin guardar.

## Procedimiento manual

1. Cierre Revit y ejecute scripts/Install-Addin.ps1 desde la raíz en PowerShell. Abra Revit 2027 y autorice la DLL local si reconoce su procedencia.
2. Complementos → Herramientas externas → BIM Family AI — Generar familia paramétrica. Si la validación falla, no se entrega el RFA: conserve el mensaje para diagnóstico.
3. Abra el archivo BIMFamilyAI_ParametricBox.rfa en Documentos/BIM Family AI/Generated. En Crear → Propiedades → Tipos de familia seleccione Cubo_500 y luego Prisma_1000x600x750; aplique y compruebe el cambio en planta y 3D.
4. En Tipos de familia escriba Ancho 1400 mm, Profundidad 800 mm y Altura 950 mm; aplique y mida. La base debe seguir en cero y X/Y deben estar centrados. No ejecute otra vez el complemento para este cambio.
5. Alterne Material entre BFAI_Rojo y BFAI_Azul. Seleccione la extrusión y compruebe la asociación y el material real; use vista sombreada para observar el color.
6. Guarde una copia de prueba, cierre y reabra. Cambie otra vez los valores y compruebe la geometría. Conserve el original generado para comparación.
7. Ejecute el comando fijo original y compruebe su cubo de 500 mm.
8. En un proyecto de prueba guardado, ejecute ambos comandos y compruebe que su estado de guardado y elementos no cambian. Repita desde un proyecto modificado y compruebe que conserva ese estado.

El control dimensional real, los materiales y la persistencia están demostrados mediante ejecución en Revit. Los doce criterios de aceptación están cubiertos por compilación, ejecución API y edición manual. Etapa 3A completada para el alcance de prueba; antes de distribuir conviene ampliar la prueba a otras plantillas y proyectos con modificaciones previas. Persisten dos advertencias MSB3277 de las dependencias instaladas; las pruebas realizadas no mostraron fallos de carga. No se avanza a 3B.
