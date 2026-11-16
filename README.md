# BIM Family AI — Etapa 5A

Cuarto comando: **Complementos → Herramientas externas → BIM Family AI — Crear familia desde fotografía**. Abre una ventana no modal con fotografía/vista previa, Meshy T2, historial, seguimiento, recuperación y creación de RFA mediante ExternalEvent.

**Integración validada por simulación; el usuario confirmó además una generación real de silla. Esta etapa 5A reutiliza el OBJ local sin enviar solicitudes Meshy.** El modo inicial es **Prueba (simulado, 0 créditos)**. Utiliza un OBJ fijo, claramente identificado; no interpreta la foto ni accede a producción. La primera generación pagada solo debe realizarla el usuario desde la interfaz, tras confirmar expresamente costo y envío.

Uso rápido: seleccione foto → Generar modelo 3D → confirme altura física real en mm y eje → Crear familia RFA → Abrir RFA en Revit. Son operaciones independientes: un fallo de Revit permite repetir el RFA con el mismo OBJ sin pagar otra generación. Sin altura conocida puede conservar el OBJ.

El perfil real inicial es T2/Smart Topology, 4000 caras, sin texturas, OBJ, **estimado 5 créditos API** según tarifa consultada el 16/11/2026 (puede cambiar). Requiere una clave propia `MESHY_API_KEY` en las variables de usuario de Windows y reiniciar Revit; no escriba claves en el chat ni en archivos del repositorio. Abrir ventana, seleccionar fotos y consultar historial no autoriza POST.

La [guía paso a paso y validación 4B](docs/stage4b-validation.md) explica la prueba gratuita, la configuración manual exacta, autorización de la primera generación real, recuperación de IDs, conciliación de envíos inciertos y límites. Consulte también la [arquitectura](docs/architecture.md).

## Servicio Meshy independiente (etapa 4A)

Integración Meshy Image to 3D implementada como biblioteca independiente de Autodesk, **validada únicamente con HTTP simulado**. No se ha creado ninguna tarea real ni consumido créditos. No necesita clave para las pruebas. En 4B se incorpora al complemento instalado.

`BimFamilyAI.Core` define los contratos; `BimFamilyAI.Meshy` prepara JPG/JPEG, PNG y WebP locales como Data URI, reserva intentos para evitar duplicados, conserva IDs en JSON, consulta estados con Retry-After y descarga/valida OBJ reutilizables mediante `ObjInspection`. El resultado construye el `ObjImportRequest` existente con unidades y dimensión física confirmadas por el usuario. El perfil inicial es Smart Topology / Meshy T2, 4000 caras, sin texturas, solo OBJ.

```powershell
dotnet run --project .\tests\BimFamilyAI.Meshy.Checks -c Release -- tests/fixtures
dotnet run --project .\tests\BimFamilyAI.Core.Checks -c Release -- tests/fixtures
```

Los tests usan manejadores HTTP en memoria y carpetas temporales propias. El almacén normal está en `%LOCALAPPDATA%\BIM Family AI\Meshy\Simulated` o `Real`, separado del repositorio. No contiene claves, base64 ni enlaces firmados. Un intento incierto permanece bloqueado hasta reconciliarlo; nunca borre el registro para resolver un timeout.

El modo real exige habilitación explícita, `MESHY_API_KEY` y confirmación de una nueva generación para la imagen/configuración concreta. La interfaz 4B entrega la importación mediante ExternalEvent. Las pruebas de desarrollo permanecen simuladas. Cancelar seguimiento local no cancela la tarea del proveedor.

Vea [arquitectura](docs/architecture.md) e [informe 4A](docs/stage4a-validation.md) para pruebas, límites, decisiones, diferencias con la documentación oficial y preparación de 4B.

## Funciones Revit implementadas (etapas 2, 3A y 3B)

Nuevo comando: **Complementos → Herramientas externas → BIM Family AI — Importar OBJ como familia**. Seleccione un OBJ local, sus unidades y eje vertical; opcionalmente indique altura objetivo en mm y confirme el nombre. La escala es uniforme y el resultado queda centrado en X/Y, con base Z=0, en Documentos/BIM Family AI/Generated. Ancho, Profundidad y Altura del OBJ son **informativos**, no controlan la malla.

Consulte [stage3b-validation.md](docs/stage3b-validation.md) para límites, materiales, pruebas y resultados reales. Se mantienen los dos generadores previos. Instale los cuatro comandos con el mismo script de abajo.

Pruebas del lector, sin dependencias de testing ni Autodesk:

```powershell
dotnet run --project .\tests\BimFamilyAI.Core.Checks -- tests/fixtures
```

Para probar el importador en Revit use `tests/fixtures/cube_500mm.obj` en milímetros o `offset_prism_m.obj` en metros, Z vertical. El segundo debe medir 1000 × 600 × 750 mm; con altura objetivo 1500 mm debe medir 2000 × 1200 × 1500 mm. Los RFA generados ya se comprobaron en Revit. Para importar modelos propios no necesita ejecutar las pruebas: seleccione el tercer comando y confirme las unidades de su archivo.

Se añade **BIM Family AI — Generar familia paramétrica** a Herramientas externas, junto al generador fijo original. Crea `BIMFamilyAI_ParametricBox.rfa` con una sola extrusión, parámetros de tipo **Ancho, Profundidad, Altura y Material**, y tipos **Cubo_500** y **Prisma_1000x600x750**. Cambie estos valores desde **Crear → Propiedades → Tipos de familia**, aplique y observe la geometría sin ejecutar de nuevo el complemento.

Los parámetros auxiliares BFAI_Semiancho y BFAI_Semiprofundidad son fórmulas para conservar el centrado. El material está asociado a la extrusión. Consulte el [informe y pruebas de etapa 3A](docs/stage3a-validation.md) para el mecanismo, resultados reales y comprobaciones pendientes.

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

Con Revit cerrado, elimina solo el manifiesto y los cuatro archivos conocidos de la aplicación (Core/Revit/Meshy y deps.json). Conserva familias, historial y archivos ajenos.

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

## Materiales y apariencia (etapa 5A)

Con un OBJ ya recuperado, marque **Materiales y apariencia** antes de Crear RFA. Puede conservar original, aplicar RGB uniforme o copiar el color gráfico de un material de la familia nueva. Se guarda otro RFA sin cambiar geometría ni generar IA. El RGB se verifica al reabrir; corresponde a Sombreado y no configura texturas ni apariencia Realista. Véase [validación y límites 5A](docs/stage5a-validation.md). La generación real de la silla fue confirmada por el usuario; esta etapa reutiliza su OBJ local sin llamadas Meshy. Etapa 5B sin iniciar.

Referencias oficiales: https://help.autodesk.com/cloudhelp/2027/ENU/Revit-API/files/Revit_API_Developers_Guide/Introduction/Getting_Started/Using_the_Autodesk_Revit_API/Revit_API_Revit_API_Developers_Guide_Introduction_Getting_Started_Using_the_Autodesk_Revit_API_Registration_of_add_ins_html.html
