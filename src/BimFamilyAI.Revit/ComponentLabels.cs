using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.UI;

namespace BimFamilyAI.Revit;

internal enum ComponentLabelAction { Selected, List, Set, Clear }
internal sealed record ComponentLabelRequest(ComponentLabelAction Action, string? ExpectedUniqueId = null,
    string? ExpectedComponentId = null, string? Name = null);
internal sealed record ComponentLabelItem(string ComponentId, string? Name, string ElementUniqueId);
internal sealed record ComponentLabelResponse(ComponentLabelItem? Selected, IReadOnlyList<ComponentLabelItem> Items);

/// <summary>Manual labels for existing 5D-B2 imports. Never changes the immutable identity schema.</summary>
internal static class ComponentLabels
{
    private static readonly Guid LabelSchemaId = new("b85ae122-22ec-49d0-936f-b73f3e630a7e");
    private const int Version = 1;

    internal static ComponentLabelResponse Execute(UIApplication application, ComponentLabelRequest request)
    {
        var uiDocument = application.ActiveUIDocument
            ?? throw new InvalidOperationException("No hay un documento activo. Abra una familia por componentes.");
        var document = uiDocument.Document;
        if (!document.IsFamilyDocument || document.OwnerFamily.FamilyCategory.Id != new ElementId(BuiltInCategory.OST_GenericModel))
            throw new InvalidOperationException("Abra el RFA por componentes en el editor de familias; un proyecto RVT no admite esta operación.");

        if (request.Action == ComponentLabelAction.List)
            return new(null, ReadAll(document));

        var selection = uiDocument.Selection.GetElementIds();
        if (selection.Count == 0) throw new InvalidOperationException("Seleccione visualmente una importación de la familia.");
        if (selection.Count != 1) throw new InvalidOperationException("Seleccione una sola importación para identificarla.");
        if (document.GetElement(selection.Single()) is not ImportInstance element || element.IsLinked || element.ViewSpecific)
            throw new InvalidOperationException("El elemento seleccionado no es una importación OBJ embebida de la familia.");
        var identity = ObjAssemblyImporter.TryReadIdentity(element)
            ?? throw new InvalidOperationException("La importación no tiene identidad de componentes BIM Family AI; el modo OBJ completo no se puede etiquetar.");

        if (request.Action is ComponentLabelAction.Set or ComponentLabelAction.Clear)
        {
            if (request.ExpectedUniqueId != element.UniqueId || request.ExpectedComponentId != identity.ComponentId)
                throw new InvalidOperationException("La selección cambió. Consulte de nuevo la pieza antes de guardar su etiqueta.");
            if (document.IsReadOnly || document.IsReadOnlyFile)
                throw new InvalidOperationException("La familia está en modo de solo lectura; no se puede guardar la etiqueta.");
            string? name = request.Action == ComponentLabelAction.Set ? ValidateName(request.Name) : null;
            using var transaction = new Transaction(document, "BIM Family AI — etiqueta manual de componente");
            if (transaction.Start() != TransactionStatus.Started)
                throw new InvalidOperationException("No se pudo iniciar la transacción de etiquetado.");
            if (name is null)
            {
                var schema = Schema.Lookup(LabelSchemaId);
                if (schema is not null) element.DeleteEntity(schema);
            }
            else
            {
                var schema = LabelSchema();
                using var entity = new Entity(schema);
                entity.Set("ComponentId", identity.ComponentId);
                entity.Set("Name", name);
                entity.Set("Version", Version);
                element.SetEntity(entity);
            }
            if (transaction.Commit() != TransactionStatus.Committed)
                throw new InvalidOperationException("Revit no confirmó el cambio de etiqueta.");
            if (ReadName(element, identity.ComponentId) != name ||
                ObjAssemblyImporter.TryReadIdentity(element) != identity)
                throw new InvalidOperationException("La etiqueta o la identidad técnica no se conservaron tras la transacción.");
        }

        var selected = new ComponentLabelItem(identity.ComponentId, ReadName(element, identity.ComponentId), element.UniqueId);
        return new(selected, ReadAll(document));
    }

    internal static IReadOnlyList<ComponentLabelItem> ReadAll(Document document)
    {
        if (!document.IsFamilyDocument)
            throw new InvalidOperationException("La lista de componentes solo está disponible en el editor de familias.");
        var items = new List<ComponentLabelItem>();
        foreach (var element in new FilteredElementCollector(document).OfClass(typeof(ImportInstance)).Cast<ImportInstance>())
        {
            var identity = ObjAssemblyImporter.TryReadIdentity(element);
            if (identity is null) continue;
            items.Add(new(identity.ComponentId, ReadName(element, identity.ComponentId), element.UniqueId));
        }
        if (items.Count == 0)
            throw new InvalidOperationException("La familia no contiene componentes BIM Family AI etiquetables.");
        if (items.Select(i => i.ComponentId).Distinct(StringComparer.Ordinal).Count() != items.Count)
            throw new InvalidOperationException("La familia contiene identificadores técnicos duplicados.");
        return items.OrderBy(i => i.ComponentId, StringComparer.Ordinal).ToArray();
    }

    internal static string? ReadName(ImportInstance element, string componentId)
    {
        var schema = Schema.Lookup(LabelSchemaId);
        if (schema is null) return null;
        var entity = element.GetEntity(schema);
        if (!entity.IsValid()) return null;
        if (entity.Get<int>("Version") != Version || entity.Get<string>("ComponentId") != componentId)
            throw new InvalidOperationException("La etiqueta no corresponde a la identidad técnica de esta pieza.");
        return ValidateName(entity.Get<string>("Name"));
    }

    internal static string ValidateName(string? value)
    {
        string name = value?.Trim() ?? "";
        if (name.Length is < 1 or > 120 || name.Any(char.IsControl))
            throw new ArgumentException("Escriba una etiqueta de 1 a 120 caracteres, sin controles ni saltos de línea.");
        return name;
    }

    private static Schema LabelSchema()
    {
        var schema = Schema.Lookup(LabelSchemaId);
        if (schema is not null) return schema;
        var builder = new SchemaBuilder(LabelSchemaId);
        builder.SetSchemaName("BFAI_ComponentLabel_v1");
        builder.SetReadAccessLevel(AccessLevel.Public);
        builder.SetWriteAccessLevel(AccessLevel.Public);
        builder.AddSimpleField("ComponentId", typeof(string));
        builder.AddSimpleField("Name", typeof(string));
        builder.AddSimpleField("Version", typeof(int));
        return builder.Finish();
    }
}
