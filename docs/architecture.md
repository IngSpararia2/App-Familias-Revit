# Arquitectura mínima

## Etapa 3B

Core añade ObjImportRequest y ObjInspection, sin Autodesk: selección de unidades/eje y lectura estructural limitada del OBJ. ImportObjFamilyCommand recoge opciones; ObjFamilyImporter realiza importación nativa, transformación, datos BIM y validación. FamilyFileWorkflow contiene el ciclo de plantilla y guardado antes incluido en GenerateTestFamilyCommand; los tres comandos lo reutilizan. La futura entrada Meshy podrá proporcionar un archivo y ObjImportRequest al mismo importador. No se implementan servicios IA en esta etapa.

## Etapa 3A

GenerateParametricFamilyCommand delega el ciclo de documento/plantilla/guardado al flujo existente de GenerateTestFamilyCommand, conservando el generador fijo. ParametricBoxBuilder concentra las restricciones y validación de una única extrusión: planos, cotas de semidimensiones con fórmulas, altura y material asociados. No añade dependencias ni lógica Autodesk a Core. El archivo temporal se reabre y se valida antes de entregar el RFA final. Ver stage3a-validation.md para resultados y pendientes.

## Límites actuales

- **BimFamilyAI.Core**: modelos de entrada/salida, validaciones y contratos puros de C#. Sin Autodesk, red ni dependencias de proveedores.
- **BimFamilyAI.Revit**: adaptador para Revit 2027 y destino `net10.0-windows` x64. Referencia Core y las DLL locales de Revit, sin redistribuirlas.
- **Servicios de IA (futuro)**: adaptadores separados para análisis de imagen/OpenAI y generación 3D/Meshy. Core definirá los datos intercambiados; claves mediante variables de entorno o configuración local ignorada por Git.
- **WPF (futuro)**: interfaz de escritorio que orquesta el flujo y presenta estado/errores sin contener lógica de Revit.

## Flujo previsto

```text
Fotografía → análisis visual → generación 3D → OBJ
                                        ↓
                         Revit API convierte/importa
                                        ↓
                                      RFA
```

La primera modalidad será una familia geométrica basada en geometría 3D importada. Más adelante podrá añadirse una modalidad paramétrica que traduzca dimensiones y características a geometría nativa de Revit. La creación y guardado de RFA ocurre dentro del adaptador Revit, que es el único módulo que debe conocer la API de Autodesk.

## Decisiones de esta etapa

- Aplicación local, sin backend ni base de datos.
- Solo se crean Core y el límite Revit; no hay proyectos vacíos para UI o proveedores.
- Las referencias API se resuelven centralmente mediante `RevitInstallDir` o `REVIT_2027_DIR` y no se copian al directorio de salida.
- No se añaden paquetes NuGet ni credenciales. En etapa 2 se incorpora `GenerateTestFamilyCommand` y el script genera el manifiesto local sin rutas versionadas.

## Entorno detectado en esta máquina

- Windows 11 x64 (build 26300).
- SDK detectados en etapa 2: 10.0.404 y 11.0.100. `global.json` selecciona .NET 10 estable con rollForward latestFeature desde 10.0.100. No se instaló software durante esta ejecución.
- Revit 2027 presente en `C:\Program Files\Autodesk\Revit 2027`; `RevitAPI.dll` y `RevitAPIUI.dll` disponibles.
- Git 2.55.0.windows.4 disponible. No se encontró `msbuild` como comando independiente; el SDK de .NET incluye MSBuild.
- En etapa 1 la carpeta estaba vacía; en etapa 2 existe repositorio Git con origin IngSpararia2/App-Familias-Revit y rama main inicialmente limpia.

## Etapa 2

El comando crea un documento independiente, comprueba Modelo genérico, convierte milímetros mediante UnitUtils y crea una extrusión sobre Z=0. Comprueba su caja geométrica antes de confirmar la transacción. SaveAs ocurre después de Commit. El guardado usa una carpeta temporal propia y un movimiento final sin sobrescritura; el documento se cierra y la carpeta temporal se limpia incluso ante errores, comunicando cualquier fallo de limpieza.

Se habilita WPF solamente para el selector estándar de archivos RFT, sin interfaz propia. La ejecución y apertura de RFA requieren comprobación dentro de Revit. Ver README para el procedimiento completo.

Revisar los requisitos actuales en la [guía oficial de migración a .NET 10 para Revit 2027](https://help.autodesk.com/cloudhelp/2027/ENU/Revit-WhatsNew/files/GUID-8D7A4715-EAF8-4BD1-BE78-061F900D0BCE.htm).
