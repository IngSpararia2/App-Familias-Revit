# BIM Family AI — Etapa 2

Comando externo para crear una familia Modelo genérico con extrusión sólida nativa de 500 × 500 × 500 mm, centrada en X/Y y apoyada en Z=0. Core permanece independiente de Autodesk.

## Compilar

Requiere Windows x64, Revit 2027 y SDK .NET 10. Autodesk recomienda 10.0.100; global.json permite bandas posteriores estables de .NET 10. Este equipo usa 10.0.404. Para soporte completo del IDE use Visual Studio 2026 (18.0+).

```powershell
dotnet restore .\BIMFamilyAI.sln
dotnet build .\BIMFamilyAI.sln -c Release -p:Platform=x64
dotnet build .\src\BimFamilyAI.Core\BimFamilyAI.Core.csproj -c Release
```

Directory.Build.targets resuelve las DLL locales con Private=false. Para otra ubicación configure REVIT_2027_DIR o pase -p:RevitInstallDir='D:\Autodesk\Revit 2027'. La ausencia de las dos DLL produce un error explícito. No copiar API ni plantillas al repositorio.

## Instalar, actualizar y desinstalar

Cierre Revit y ejecute en PowerShell desde la raíz:

```powershell
.\scripts\Install-Addin.ps1
```

El script compila Release x64, copia solo archivos propios a %LOCALAPPDATA%\BIM Family AI\Revit2027 y genera %APPDATA%\Autodesk\Revit\Addins\2027\BIMFamilyAI.addin. Esta ruta se verificó con RevitAddInUtility.dll de la instalación local. Repetir el script actualiza el mismo registro con GUID fijo, sin duplicarlo ni requerir administrador.

Abra Revit y autorice el complemento local sin firma si reconoce su procedencia. Ejecute Complementos → Herramientas externas → BIM Family AI. Cierre la página Inicio con su X si oculta la cinta.

```powershell
.\scripts\Install-Addin.ps1 -Uninstall
```

Con Revit cerrado, elimina solo el manifiesto y los tres archivos conocidos de la aplicación. Conserva las familias generadas.

## Plantilla y salida

El comando consulta Application.FamilyTemplatePath y busca una única plantilla estándar en español/inglés. Si no puede decidir, abre un selector RFT. Puede configurar BIMFAMILYAI_TEMPLATE_PATH con la ruta completa antes de iniciar Revit. Se valida existencia y extensión; Revit valida el formato al crear el documento y el comando comprueba su categoría antes de modificarlo.

Se detectó contenido en C:\ProgramData\Autodesk\RVT 2027\Family Templates, incluyendo English\Metric Generic Model.rft. Seleccione Modelo genérico sin anfitrión; evite plantillas adaptativas o basadas en muro/cara. Si faltan, instale contenido oficial de Revit 2027 y configure las plantillas en las opciones de Revit.

La salida usa Documentos del usuario obtenido mediante Windows, subcarpeta BIM Family AI\Generated. Nombre inicial BIMFamilyAI_TestCube_500mm.rfa; duplicados reciben _1, _2, etc. Se guarda en una carpeta temporal propia y se mueve sin sobrescribir. Se cierra el documento independiente; los errores de limpieza se comunican.

## Prueba manual en Revit

1. Ejecute el comando y compruebe el mensaje y archivo RFA.
2. Repita: deben existir dos archivos diferentes, conservando el primero.
3. Abra el RFA mediante Archivo → Abrir → Familia y abra su vista 3D.
4. Seleccione la extrusión sólida. En planta mida ancho y profundidad de 500 mm (de -250 a +250 respecto a los planos centrales); en alzado compruebe base 0 y altura 500 mm. Ajuste las unidades de visualización a mm si es necesario.
5. Compruebe Modelo genérico en Categoría y parámetros de familia. Cierre sin guardar cambios de inspección.
6. Repita desde un proyecto de prueba abierto y compruebe que no cambia su estado de modificación.

El comando verifica la caja de la extrusión antes de confirmar la transacción. Compilar no demuestra apertura o ejecución dentro de Revit.

## Problemas

- SDK: dotnet --version desde esta carpeta debe devolver 10.x; instale un SDK .NET 10 estable autorizado si falta. No se instaló software en esta entrega.
- MSB3277: se observaron dos advertencias entre dependencias de Autodesk y .NET incluso con SDK 10.0.404; no se suprimen. Revisar la carga antes de distribuir.
- Comando ausente: reinicie Revit, revise Assembly en el manifiesto y compruebe el administrador de complementos.
- Plantilla inválida: seleccione la estándar sin anfitrión de Revit 2027.
- Guardado fallido: revise permisos de Documentos y el detalle mostrado en el diálogo.

## Etapa 3

Pendiente de definir e implementar después de validar esta entrega. IA, OBJ, WPF completo y parámetros editables quedan pendientes. Para claves futuras usar variables de entorno o configuración local excluida de Git; nunca guardar secretos en código.

Referencias oficiales: https://help.autodesk.com/cloudhelp/2027/ENU/Revit-API/files/Revit_API_Developers_Guide/Introduction/Getting_Started/Using_the_Autodesk_Revit_API/Revit_API_Revit_API_Developers_Guide_Introduction_Getting_Started_Using_the_Autodesk_Revit_API_Registration_of_add_ins_html.html
