# Arquitectura mínima

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
- No se añaden paquetes NuGet, credenciales, comandos Revit ni manifiestos `.addin`.

## Entorno detectado en esta máquina

- Windows 11 x64 (build 26300).
- .NET SDK instalado: 11.0.100; runtimes .NET 10.0.15 y .NET 11.0.0. No está instalado el SDK 10.0.100 que Autodesk indica para crear complementos Revit 2027. No se instaló software durante esta etapa.
- Revit 2027 presente en `C:\Program Files\Autodesk\Revit 2027`; `RevitAPI.dll` y `RevitAPIUI.dll` disponibles.
- Git 2.55.0.windows.4 disponible. No se encontró `msbuild` como comando independiente; el SDK de .NET incluye MSBuild.
- Carpeta inicial vacía, sin proyecto ni repositorio Git.

Revisar los requisitos actuales en la [guía oficial de migración a .NET 10 para Revit 2027](https://help.autodesk.com/cloudhelp/2027/ENU/Revit-WhatsNew/files/GUID-8D7A4715-EAF8-4BD1-BE78-061F900D0BCE.htm).
