using Autodesk.Revit.DB;

namespace BimFamilyAI.Revit;

/// <summary>Only used in a newly created family containing one OBJ import.</summary>
internal static class ImportedMaterials
{
    internal sealed record Region(ElementId MaterialId, ElementId CategoryId);
    internal sealed record Editable(ElementId Id,string Name,byte Red,byte Green,byte Blue,int References);
    internal sealed record Assignment(ElementId Id,string OriginalName,byte Red,byte Green,byte Blue);
    internal sealed record SavedMaterial(string Name,byte Red,byte Green,byte Blue,int Transparency,bool RenderShading,int References);
    internal static IReadOnlyList<Region> Inspect(Document doc)
    {
        var imports=new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>().ToArray();
        if(imports.Length!=1)throw new InvalidOperationException("Se requiere una única importación OBJ en la familia nueva.");
        return InspectElement(doc,imports[0]);
    }
    internal static IReadOnlyList<Region> InspectElement(Document doc,ImportInstance imported)
    {
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
        Walk(imported.get_Geometry(new Options {DetailLevel=ViewDetailLevel.Fine}));
        return regions.ToArray();
    }
    internal static IReadOnlyList<Editable> EditableMaterials(Document doc)
    {
        return Inspect(doc).GroupBy(r=>r.MaterialId).Select(group=>
        {
            var material=doc.GetElement(group.Key) as Material;
            return material is null?null:new Editable(group.Key,material.Name,material.Color.Red,material.Color.Green,material.Color.Blue,group.Count());
        }).Where(item=>item is not null).Cast<Editable>().OrderBy(item=>item.Name,StringComparer.Ordinal).ToArray();
    }
    internal static IReadOnlyList<SavedMaterial> Snapshot(Document doc)
    {
        var regions=Inspect(doc);
        if(regions.Count==0)throw new InvalidOperationException("La importación no contiene regiones materiales verificables.");
        return regions.GroupBy(r=>r.MaterialId).Select(group=>
        {
            if(doc.GetElement(group.Key) is not Material m)throw new InvalidOperationException("La referencia material desapareció.");
            return new SavedMaterial(m.Name,m.Color.Red,m.Color.Green,m.Color.Blue,m.Transparency,m.UseRenderAppearanceForShading,group.Count());
        }).OrderBy(item=>item.Name,StringComparer.Ordinal).ToArray();
    }
    internal static void ValidateSnapshot(Document doc,IReadOnlyList<SavedMaterial> expected)
    {
        var actual=Snapshot(doc);
        if(actual.Count!=expected.Count || !actual.SequenceEqual(expected))
            throw new InvalidOperationException("Los materiales o sus referencias cambiaron al guardar y reabrir la familia.");
    }
    internal static void ApplyMultiple(Document doc,IReadOnlyList<Assignment> assignments,IReadOnlySet<ElementId> originalMaterials)
    {
        var editable=EditableMaterials(doc);
        if(editable.Count<2)throw new InvalidOperationException("El OBJ no contiene dos materiales explícitos independientes.");
        if(editable.Any(m=>originalMaterials.Contains(m.Id)))throw new InvalidOperationException("La importación comparte un material de plantilla; no se modificarán sus referencias.");
        if(assignments.Count==0)throw new InvalidOperationException("Seleccione al menos un material para editar.");
        if(assignments.Select(a=>a.Id).Distinct().Count()!=assignments.Count)throw new InvalidOperationException("Un material fue seleccionado dos veces.");
        foreach(var selected in assignments)
        {
            var current=editable.SingleOrDefault(m=>m.Id==selected.Id);
            if(current is null || current.Name!=selected.OriginalName)throw new InvalidOperationException("La referencia de material cambió antes de aplicar colores.");
            if(originalMaterials.Contains(selected.Id))throw new InvalidOperationException("El material pertenece a la plantilla y no se modificará.");
        }
        using var transaction=new Transaction(doc,"Colores independientes de OBJ");
        if(transaction.Start()!=TransactionStatus.Started)throw new InvalidOperationException("No se pudo iniciar la edición de materiales.");
        int index=0;
        foreach(var item in editable)
        {
            var material=(Material)doc.GetElement(item.Id);
            // Revit merges imported materials with the same name when families meet in one RVT.
            string suffix=item.Name[..Math.Min(item.Name.Length,30)];
            string isolated=$"BFAI_Region{++index}_{suffix}_{Guid.NewGuid():N}";
            if(!Material.IsNameUnique(doc,isolated))throw new InvalidOperationException("No se pudo aislar el nombre de un material importado.");
            material.Name=isolated;
            var selected=assignments.SingleOrDefault(a=>a.Id==item.Id);
            if(selected is null)continue; // Keep untouched material shading, RGB and transparency.
            material.UseRenderAppearanceForShading=false;
            material.Color=new Color(selected.Red,selected.Green,selected.Blue);
            material.Transparency=0;
        }
        doc.Regenerate();
        foreach(var selected in assignments)
        {
            var m=(Material)doc.GetElement(selected.Id);
            if(m.Color.Red!=selected.Red || m.Color.Green!=selected.Green || m.Color.Blue!=selected.Blue || m.UseRenderAppearanceForShading || m.Transparency!=0)
                throw new InvalidOperationException("Revit no aplicó un color seleccionado.");
        }
        if(transaction.Commit()!=TransactionStatus.Committed)throw new InvalidOperationException("Revit no confirmó los materiales independientes.");
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


