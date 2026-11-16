# Validación de etapa 2

## Verificado

- Rama main inicialmente limpia, origin apunta al repositorio solicitado.
- SDK 10.0.404 seleccionado mediante global.json; también existe 11.0.100.
- Restore y build Release x64 de la solución: cero errores, dos advertencias MSB3277 entre dependencias de Autodesk y referencias .NET (no suprimidas).
- Core Release independiente: cero errores y cero advertencias.
- Referencias locales RevitAPI y RevitAPIUI de la instalación 2027 disponibles; no se encontraron RevitAPI*.dll en src ni en la salida desplegada.
- Prueba negativa con RevitInstallDir inexistente: error explícito de configuración, como se esperaba.
- Script de instalación ejecutado: manifiesto XML válido, clase y GUID definidos, ensamblado existente y hash idéntico al compilado.
- Utilidad Autodesk RevitAddInUtility instalada confirma CurrentUserAddInFolder en AppData/Roaming/Autodesk/Revit/AddIns/2027.
- Revit 2027.2 inició y presentó aviso de complemento sin firma con nombre BIM Family AI y la ruta correcta.
- git diff --check sin errores de espacios (Git informa conversión LF/CRLF).

## Integración ejecutada en Revit 2027.2

Después de que el usuario autorizó Cargar una vez, se ejecutó el comando desde Complementos → Herramientas externas → BIM Family AI dos veces, sin documento activo.

- Primera salida: `C:\Users\itopa\Documents\BIM Family AI\Generated\BIMFamilyAI_TestCube_500mm.rfa`, 454656 bytes.
- Segunda salida: `BIMFamilyAI_TestCube_500mm_1.rfa`, 454656 bytes.
- SHA-256 del primer archivo antes y después de la segunda ejecución: `C7E5C5E97805F98322150D6D65B0DD45B30D1B8C64FE30D453602C1B5DE23FD6`. Coincidencia exacta: no fue sobrescrito.
- SHA-256 del segundo archivo: `64F1BEEB36ECD9FBF04799F13D2C6D5747B4D259A543F8DED18600F69A38C5CE`.
- Ambas ejecuciones mostraron éxito y pasaron la comprobación API de caja geométrica: X/Y de -250 a +250 mm y Z de 0 a 500 mm.
- Se abrió el primer RFA correctamente en Revit y se inspeccionó su vista 3D. La selección muestra Extrusión, categoría Modelos genéricos, Sólido, inicio de extrusión 0.0 y final 500.0. No se modificaron dimensiones durante la inspección.
- No quedaron carpetas temporales de generación en Generated.

Queda pendiente comprobar la ejecución con un proyecto activo y su estado de modificación. Las pruebas realizadas desde una sesión sin documento activo no demuestran ese criterio. No se realizaron mediciones manuales independientes de ancho/profundidad; esos valores se validaron mediante la caja geométrica del comando durante la ejecución real.

El script usa destino y GUID estables para actualizaciones; la instalación repetida todavía debe ejecutarse y comprobarse con Revit cerrado. No se avanzó a etapa 3.
