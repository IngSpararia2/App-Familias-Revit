using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace BimFamilyAI.Revit;

[Transaction(TransactionMode.Manual)]
public sealed class CreateFamilyFromPhotoCommand : IExternalCommand
{
    private static PhotoFamilyWindow? window;
    public Result Execute(ExternalCommandData commandData,ref string message,ElementSet elements)
    {
        if(window is not null)
        {
            if(window.WindowState==System.Windows.WindowState.Minimized)window.WindowState=System.Windows.WindowState.Normal;
            window.Activate();return Result.Succeeded;
        }
        PhotoFamilyExternalEvent? bridge=null;
        try
        {
            bridge=new(commandData.Application);
            window=new(bridge,commandData.Application.MainWindowHandle);
            bridge.CloseWindowForShutdown=window.CloseForShutdown;
            window.Closed+=(_,_)=>window=null;
            window.Show();return Result.Succeeded;
        }
        catch(Exception)
        {
            bridge?.ReleaseInApiContext();window=null;
            message="No se pudo abrir el generador de fotografías. Revise la instalación de las DLL propias.";
            TaskDialog.Show("BIM Family AI",message);return Result.Failed;
        }
    }
}
