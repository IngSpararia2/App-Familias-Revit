# BIM Family AI

Base técnica de una aplicación local para preparar familias de Autodesk Revit a partir de fotografías. Esta entrega cubre únicamente la etapa 1: estructura, configuración y documentación. No genera familias ni contiene comandos o manifiestos de instalación para Revit.

## Requisitos

- Windows x64.
- Autodesk Revit 2027 para compilar y, en etapas posteriores, ejecutar la integración.
- Autodesk indica instalar el SDK .NET **10.0.100** para crear complementos de Revit 2027. El proyecto apunta a `net10.0`; no usa .NET 8. La versión presente en este equipo se registra abajo.
- Git y Visual Studio 2022 o Build Tools con soporte de .NET.

## Estructura

```text
BIMFamilyAI.sln
Directory.Build.props           # ajustes comunes
Directory.Build.targets         # resolución opcional de las DLL de Revit
docs/architecture.md
src/
  BimFamilyAI.Core/              # modelos y lógica sin dependencia de Revit
  BimFamilyAI.Revit/              # límite de integración Revit 2027
```

La separación de servicios de IA se mantiene como límite arquitectónico documentado; no se crea un proyecto vacío antes de que exista código que lo necesite.

## Configurar Revit API

`Directory.Build.targets` busca primero la propiedad MSBuild `RevitInstallDir`, después la variable de entorno `REVIT_2027_DIR` y finalmente la ruta estándar `C:\Program Files\Autodesk\Revit 2027` si existe. Las referencias tienen `Private=false`: las DLL propietarias no se copian al resultado.

Si Revit está en otra ruta, configure la variable para la sesión:

```powershell
$env:REVIT_2027_DIR = 'D:\Autodesk\Revit 2027'
```

O pase la propiedad en la compilación:

```powershell
dotnet build .\BIMFamilyAI.sln -p:RevitInstallDir='D:\Autodesk\Revit 2027'
```

No agregue las DLL de Autodesk al repositorio. Si no están disponibles, Core sigue siendo independiente y compilable; el proyecto Revit no podrá compilar hasta configurar las dos referencias.

## Compilar y validar

Desde la raíz:

```powershell
dotnet restore .\BIMFamilyAI.sln
dotnet build .\BIMFamilyAI.sln -c Debug -p:Platform=x64
dotnet build .\src\BimFamilyAI.Core\BimFamilyAI.Core.csproj -c Release
```

El proyecto Revit referencia API locales y requiere el SDK .NET 10 compatible. Compilar no verifica el funcionamiento dentro de Revit; la carga y las pruebas de API se harán cuando haya comandos en la etapa 2.

## Configuración futura de claves

No se almacenan claves aún. Para desarrollo local, usar variables de entorno (por ejemplo `OPENAI_API_KEY` y `MESHY_API_KEY`) o un archivo local excluido de Git. Nunca poner secretos en código, `appsettings.json` versionado, capturas ni logs. Las integraciones externas quedan fuera de esta etapa.

## Siguiente etapa

Consultar [docs/architecture.md](docs/architecture.md) y crear en etapa 2 el primer comando Revit y su mecanismo de despliegue conforme al SDK instalado.
