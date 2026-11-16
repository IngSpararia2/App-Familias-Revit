# Etapa 5B — Interfaz WPF y experiencia de usuario

## Objetivo y cambios

La modernización se limita al cuarto comando. `PhotoFamilyWindow.cs` organiza el recorrido en cuatro secciones desplazables: fotografía/configuración, generación/seguimiento, preparación BIM y resultado. La fase actual permanece visible arriba y el estado/progreso y contador simulado permanecen visibles abajo. Los botones principales tienen mayor jerarquía y el diálogo `ImportedMaterialDialog.cs` presenta opciones más descriptivas, RGB y una muestra de color. La ventana limita su tamaño inicial según el área útil de Windows y conserva desplazamiento vertical. No se añadieron bibliotecas, Ribbon, MVVM ni nuevos motores.

La selección de eje dice **eje vertical ORIGINAL del OBJ** y aclara que Revit recibe Z. Algunos OBJ de Meshy pueden usar Y; el usuario debe comprobarlo. Cambiar eje, altura o color y crear otro RFA reutiliza el OBJ. No se intenta inferir el eje por la fotografía ni se cambia la rotación del importador.

Una **nueva generación real** sigue exigiendo fotografía válida, configuración válida, credencial y autorización explícita de costo. El modo inicial sigue simulado. Consultar ID, continuar una tarea, descargar/reutilizar OBJ, crear o abrir RFA y cambiar datos BIM no crean otra generación. El botón de reintento de rechazo permanece en opciones avanzadas, requiere imagen/configuración válidas, registro rechazado y respeta Retry-After; en modo real solicita una autorización nueva. La conciliación solo se habilita con reserva incierta seleccionada e ID válido. Los mecanismos del servicio y el diario JSON no cambiaron.

El historial muestra estado, ocho caracteres iniciales del ID, fecha local cuando existe y si hay OBJ validado. Al seleccionar un registro se conserva el ID completo en el campo para consulta/copia. Los botones Crear RFA y Abrir RFA exigen respectivamente OBJ existente más nombre/altura positivos, y archivo RFA existente. Las acciones se deshabilitan durante una operación para evitar simultaneidad. Los errores siguen visibles en el panel inferior junto al ID útil para recuperación.

El diálogo de materiales conserva tres opciones: apariencia original, RGB gráfico uniforme o copiar solo el RGB de un material disponible en la nueva familia. La muestra indica el RGB seleccionado. El efecto garantizado es **Sombreado**; Realista puede presentar otra apariencia. El color uniforme no convierte la silla en piezas independientes. Cancelar el diálogo impide publicar el nuevo RFA.

## Archivos y compatibilidad

- Modificados: `src/BimFamilyAI.Revit/PhotoFamilyWindow.cs`, `src/BimFamilyAI.Revit/ImportedMaterialDialog.cs`, `README.md`.
- Creado: `docs/stage5b-validation.md`.
- Sin cambios: MeshyClient, MeshyOptions, PhotoGenerationSession, JsonTaskStore, Core, ObjInspection, ObjFamilyImporter, ParametricBoxBuilder, ImportedMaterials, UniformObjMaterial, FamilyFileWorkflow, ExternalEvent, instalador, GUID y tres comandos anteriores.

## Validación ejecutada el 16/11/2026

| Comprobación | Resultado |
|---|---|
| Solución Release/x64 | 0 errores; 2 MSB3277 preexistentes de referencias Autodesk (`Microsoft.VisualBasic` y `System.Drawing`). |
| Core.Checks | 11/11. |
| Meshy.Checks | 187/187, transporte HTTP simulado. |
| Instalación local | `scripts/Install-Addin.ps1` ejecutado con Revit cerrado; SHA256 de la DLL desplegada coincide con la compilada. Cuatro comandos visibles en Herramientas externas. |
| Revit 2027.2, cuarto comando | Ventana abre, muestra cuatro fases y controles de generación deshabilitados sin foto. Historial local mostró ID/estado/fecha/OBJ. |
| Recuperación | Tarea simulada existente `b8487e43-0dd1-491e-bab9-b0f49332e58c`: Continuar recuperó OBJ local; contador POST=0, GET=0, descargas=0, créditos=0. |
| RFA y material | Altura 750 mm, eje original Z del OBJ simulado, RGB uniforme 0,80,220. Se creó y reabrió `SIM_Familia_Foto_4.rfa` (454656 bytes), ruta nueva sin sobrescritura. |
| Abrir y reabrir | Botón Abrir RFA abrió la familia en Revit; ventana cerró y volvió a abrir desde el cuarto comando con modo simulado inicial. |
| Git | `git diff --check` sin errores; avisos de conversión LF/CRLF de Git. Sin commit ni cambios remotos. |

No se enviaron solicitudes Meshy de producción, no se creó una tarea de IA y no se consumieron créditos. El RFA de silla `Silla_Comedor_Linda_Corregido.rfa` conservó su hash SHA256 `F6C7BF555C4B09D280D80824062D685E2C06D20B692965FFE18815037E614463`. La prueba de UI se hizo sobre el proyecto propio de validación 5A, sin tocar un proyecto real del usuario.

La revisión visual fue en la resolución disponible de escritorio (1520 × 800). Los paneles inferiores fueron accesibles mediante la barra de desplazamiento; se verificó el diálogo de materiales y su muestra RGB. No se ejecutó una medición separada en monitores 1366 × 768 con escalado 125%/150%; el diseño limita dimensiones iniciales al área útil y mantiene el desplazamiento, pero esa comprobación visual queda pendiente en esas configuraciones. Tampoco se repitieron las 47 aserciones geométricas 5A, ya que los motores no cambiaron. La siguiente etapa recomendable, solo si se solicita, es validar el escalado de interfaz en esos monitores y estudiar apariencias Realistas/texturas como trabajo separado.
