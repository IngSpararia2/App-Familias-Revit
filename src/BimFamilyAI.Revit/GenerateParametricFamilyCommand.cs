using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace BimFamilyAI.Revit;

[Transaction(TransactionMode.Manual)]
public sealed class GenerateParametricFamilyCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        => FamilyFileWorkflow.ExecuteFamily(commandData, ref message, true);
}
