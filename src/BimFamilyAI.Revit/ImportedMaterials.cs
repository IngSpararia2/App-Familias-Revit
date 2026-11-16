using Autodesk.Revit.DB;

namespace BimFamilyAI.Revit;

/// <summary>Only used in a newly created family containing one OBJ import.</summary>
internal static class ImportedMaterials
{
    internal sealed record Region(ElementId MaterialId, ElementId CategoryId);
    internal static IReadOnlyList<Region> Inspect(Document doc)
    {
        var imports=new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>().ToArray();
        if(imports.Length!=1)throw new InvalidOperationException("Se requiere una única importación OBJ en la familia nueva.");
        var regions=new HashSet<Region>();
        void Walk(GeometryElement geometry)
        {
            foreach(var item in geometry)
            {
                if(item is GeometryInstance instance) {Walk(instance.GetInstanceGeometry());continue;}
                var category=(doc.GetElement(item.GraphicsStyleId) as GraphicsStyle)?.GraphicsStyleCategory;
                if(item is Mesh mesh)regions.Add(new(mesh.MaterialElementId,category?.Id ?? ElementId.InvalidElementId));
                if(item is Solid solid)foreach(Face face in solid.Faces)regions.Add(new(face.MaterialElementId,category?.Id ?? ElementId.InvalidElementId));
            }
        }
        Walk(imports[0].get_Geometry(new Options {DetailLevel=ViewDetailLevel.Fine}));
        return regions.ToArray();
    }
    internal static void Apply(Document doc, string name, byte red, byte green, byte blue, IReadOnlySet<ElementId> originalMaterials, BimFamilyAI.Core.ObjImportRequest? request = null)
    {
        var regions=Inspect(doc);
        if(regions.Any(r=>doc.GetElement(r.MaterialId) is not Material))
        {
            if(request is null)throw new InvalidOperationException("Se necesita el OBJ original para preparar un material explícito seguro.");
            using var prepared=UniformObjMaterial.Prepare(request.Path,red,green,blue);
            var imported=new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Single();
            using(var remove=new Transaction(doc,"Reemplazar importación propia por copia con MTL"))
            {remove.Start();doc.Delete(imported.Id);if(remove.Commit()!=TransactionStatus.Committed)throw new InvalidOperationException("No se pudo preparar la importación con material.");}
            ObjFamilyImporter.Import(doc,request with {Path=prepared.Path});
            Apply(doc,name,red,green,blue,originalMaterials);
            return;
        }
        if(regions.Count==0 || regions.Any(r=>doc.GetElement(r.MaterialId) is not Material && doc.GetElement(r.CategoryId) is not GraphicsStyle && Category.GetCategory(doc,r.CategoryId) is null))
            throw new InvalidOperationException("Esta importación no expone materiales o categorías editables. Conserve su apariencia original.");
        if(regions.Any(r=>originalMaterials.Contains(r.MaterialId)))
            throw new InvalidOperationException("La malla comparte un material previo de la plantilla. No se modificará; conserve la apariencia original.");
        using var transaction=new Transaction(doc,"Material gráfico uniforme de OBJ");transaction.Start();
        string stem="BFAI_"+name+"_"+red+"_"+green+"_"+blue;
        if(!Material.IsNameUnique(doc,stem))throw new InvalidOperationException("El nombre del material ya existe. Indique otro nombre.");
        int index=0;
        foreach(var id in regions.Select(r=>r.MaterialId).Distinct())if(doc.GetElement(id) is Material material)
        {
            // These IDs did not exist before this single OBJ was imported. Never edit a template material.
            material.Name=stem+"_Region"+(++index);Set(material);
        }
        doc.Regenerate();Validate(doc,red,green,blue);
        if(transaction.Commit()!=TransactionStatus.Committed)throw new InvalidOperationException("Revit no confirmó el material.");
        void Set(Material m){m.UseRenderAppearanceForShading=false;m.Color=new Color(red,green,blue);m.Transparency=0;}
    }
    internal static void Validate(Document doc,byte r,byte g,byte b)
    {
        var regions=Inspect(doc);
        if(regions.Count==0)throw new InvalidOperationException("No hay regiones de material.");
        foreach(var region in regions)
        {
            var m=doc.GetElement(region.MaterialId) as Material ?? Category.GetCategory(doc,region.CategoryId)?.Material;
            if(m is null || m.UseRenderAppearanceForShading || m.Color.Red!=r || m.Color.Green!=g || m.Color.Blue!=b || m.Transparency!=0)
                throw new InvalidOperationException("El material gráfico no persistió en la geometría importada.");
        }
    }
}


