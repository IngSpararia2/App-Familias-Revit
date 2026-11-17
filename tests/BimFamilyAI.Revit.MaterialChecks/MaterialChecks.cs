using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using BimFamilyAI.Core;
using BimFamilyAI.Revit;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

// Explicit local integration runner: register manually only in a test Revit session.
public sealed class MaterialChecks : IExternalApplication
{
 public Result OnStartup(UIControlledApplication app){app.Idling+=Run;return Result.Succeeded;}
 public Result OnShutdown(UIControlledApplication app)=>Result.Succeeded;
 void Run(object? sender,IdlingEventArgs args)
 {
  var ui=(UIApplication)sender!;ui.Idling-=Run;
  var folder=new DirectoryInfo(Path.GetDirectoryName(typeof(MaterialChecks).Assembly.Location)!);
  while(folder is not null && !File.Exists(Path.Combine(folder.FullName,"BIMFamilyAI.sln")))folder=folder.Parent;
  if(folder is null)throw new DirectoryNotFoundException("No se encontró la solución para los fixtures de materiales.");
  string root=folder.FullName;
  string output=Path.Combine(root,"artifacts","stage5c-"+DateTime.Now.ToString("yyyyMMdd-HHmmss"));Directory.CreateDirectory(output);
  var log=new List<object>();Document? doc=null;int passed=0;
  void Check(bool condition,string label){if(!condition)throw new Exception(label);passed++;log.Add(new {pass=label});}
  string Signature(Document d){var lines=new List<string>();void Walk(GeometryElement g){foreach(var item in g){if(item is GeometryInstance i)Walk(i.GetInstanceGeometry());if(item is Mesh m){lines.Add("tri="+m.NumTriangles);foreach(var p in m.Vertices)lines.Add(p.ToString());}}}foreach(ImportInstance i in new FilteredElementCollector(d).OfClass(typeof(ImportInstance)))Walk(i.get_Geometry(new Options()));return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("|",lines))));}
  try
  {
   doc=ui.Application.NewFamilyDocument(@"C:\ProgramData\Autodesk\RVT 2027\Family Templates\English\Metric Generic Model.rft");
   var sharedRequest=new ObjImportRequest(Path.Combine(root,"tests/fixtures/shared_material.obj"),ObjUnit.Millimeters,ObjVerticalAxis.Z,500,"Stage5C_Shared","Shared material fixture");
   ObjFamilyImporter.Import(doc,sharedRequest);
   var shared=ImportedMaterials.EditableMaterials(doc);var sharedIds=ImportedMaterials.Inspect(doc).Select(r=>r.MaterialId).Distinct().ToArray();
   log.Add(new {fixture="shared_material.obj",materials=shared.Select(m=>new {m.Name,id=m.Id.Value,m.References}).ToArray()});
   Check(shared.Count==sharedIds.Count(id=>doc.GetElement(id) is Material),"Editable choices correspond to actual Revit material IDs");
   if(shared.Count<2)
   {
    bool rejectedShared=false;
    try{ImportedMaterials.ApplyMultiple(doc,[new(ElementId.InvalidElementId,"sin material",1,2,3)],new HashSet<ElementId>());}catch(InvalidOperationException){rejectedShared=true;}
    Check(rejectedShared,"Reject multi edit without two explicit materials");
   }
   doc.Close(false);doc=null;
   var paths=new List<string>();
   foreach(var (label,file,color,axis) in new[]{("Blue","cube_500mm.obj",(0,40,255),ObjVerticalAxis.Z),("Red","cube_500mm.obj",(240,20,20),ObjVerticalAxis.Z),("Original","cube_500mm.obj",(-1,0,0),ObjVerticalAxis.Z),("Multi","material_regions.obj",(-2,0,0),ObjVerticalAxis.Z),("MultiUniform","material_regions.obj",(240,20,20),ObjVerticalAxis.Z),("MultiTwo","material_regions.obj",(-2,0,0),ObjVerticalAxis.Z),("MultiSingle","material_regions.obj",(-2,0,0),ObjVerticalAxis.Z),("Chair","chair.obj",(0,40,255),ObjVerticalAxis.Y)})
   {
    string source=label=="Chair"?Path.Combine(root,"artifacts/stage5probe/chair.obj"):Path.Combine(root,"tests/fixtures",file);
    doc=ui.Application.NewFamilyDocument(@"C:\ProgramData\Autodesk\RVT 2027\Family Templates\English\Metric Generic Model.rft");
    var originals=new FilteredElementCollector(doc).OfClass(typeof(Material)).ToElementIds().ToHashSet();
    var originalColors=originals.ToDictionary(id=>id,id=>((Material)doc.GetElement(id)).Color);
    var request=new ObjImportRequest(source,ObjUnit.Millimeters,axis,label=="Chair"?750:500,"Stage5A_"+label,"Material integration test");
    ObjFamilyImporter.Import(doc,request);string geometry=Signature(doc);var regions=ImportedMaterials.Inspect(doc);
    log.Add(new {label,regions=regions.Select(r=>new {material=r.MaterialId.Value,category=r.CategoryId.Value}).ToArray()});
    if(color.Item1>=0)ImportedMaterials.Apply(doc,label,(byte)color.Item1,(byte)color.Item2,(byte)color.Item3,originals,request);
    if(label=="Multi")
    {
     var ids=regions.Select(r=>r.MaterialId).Distinct().ToArray();Check(ids.Length==2 && ids.All(id=>doc.GetElement(id) is Material),"Two explicit MTL regions");
     using var t=new Transaction(doc,"Independent region");t.Start();((Material)doc.GetElement(ids[0])).Color=new Color(0,40,255);((Material)doc.GetElement(ids[0])).UseRenderAppearanceForShading=false;t.Commit();
     Check(((Material)doc.GetElement(ids[1])).Color.Blue!=255,"Second region unchanged");
    }
    IReadOnlyList<ImportedMaterials.SavedMaterial>? expectedMaterials=null;
    if(label is "MultiTwo" or "MultiSingle")
    {
     var editable=ImportedMaterials.EditableMaterials(doc);Check(editable.Count==2,"5C two editable explicit materials");
     var protectedId=originals.First();var protectedMaterial=(Material)doc.GetElement(protectedId);var protectedColor=protectedMaterial.Color;
     bool rejectedTemplate=false;
     try{ImportedMaterials.ApplyMultiple(doc,[new(protectedId,protectedMaterial.Name,1,2,3)],originals);}catch(InvalidOperationException){rejectedTemplate=true;}
     Check(rejectedTemplate && protectedMaterial.Color.Red==protectedColor.Red && protectedMaterial.Color.Green==protectedColor.Green && protectedMaterial.Color.Blue==protectedColor.Blue,"Template material untouched after invalid assignment");
     var before=ImportedMaterials.Snapshot(doc);
     var assignments=new List<ImportedMaterials.Assignment>{new(editable[0].Id,editable[0].Name,240,20,20)};
     if(label=="MultiTwo")assignments.Add(new(editable[1].Id,editable[1].Name,0,40,255));
     ImportedMaterials.ApplyMultiple(doc,assignments,originals);expectedMaterials=ImportedMaterials.Snapshot(doc);
     Check(expectedMaterials.Count==2 && expectedMaterials[0].Red==240 && (label=="MultiTwo"?expectedMaterials[1].Blue==255:expectedMaterials[1].Red==before[1].Red && expectedMaterials[1].Green==before[1].Green && expectedMaterials[1].Blue==before[1].Blue),label+" independent colors");
     Check(expectedMaterials.Select(m=>m.Name).Distinct().Count()==2 && expectedMaterials.All(m=>m.Name.StartsWith("BFAI_Region")),label+" isolated material names");
    }
    if(label=="Original")Check(new FilteredElementCollector(doc).OfClass(typeof(Material)).GetElementCount()==originals.Count,"Preserve adds no materials");
    Check(Signature(doc)==geometry,label+" geometry unchanged");
    Check(originalColors.All(p=>{var c=((Material)doc.GetElement(p.Key)).Color;return c.Red==p.Value.Red && c.Green==p.Value.Green && c.Blue==p.Value.Blue;}),label+" template materials unchanged");
    using(var t=new Transaction(doc,"Shaded test view")){t.Start();var v=View3D.CreateIsometric(doc,new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(v=>v.ViewFamily==ViewFamily.ThreeDimensional).Id);v.Name="Stage5A_Shaded";v.DisplayStyle=DisplayStyle.Shading;t.Commit();}
    string path=Path.Combine(output,"Stage5A_"+label+".rfa");doc.SaveAs(path,new SaveAsOptions{OverwriteExistingFile=false});doc.Close(false);doc=null;
    doc=ui.Application.OpenDocumentFile(path);ObjFamilyImporter.ValidateSaved(doc,request);Check(Signature(doc)==geometry,label+" reopened geometry and dimensions");
    if(color.Item1>=0){ImportedMaterials.Validate(doc,(byte)color.Item1,(byte)color.Item2,(byte)color.Item3);Check(true,label+" persisted material RGB");}
    if(label=="Multi")Check(ImportedMaterials.Inspect(doc).Select(r=>r.MaterialId).Distinct().Count()==2,"Two material references persist");
    if(expectedMaterials is not null){ImportedMaterials.ValidateSnapshot(doc,expectedMaterials);Check(true,label+" reopened materials and references");}
    doc.Close(false);doc=null;paths.Add(path);
   }
   // All native workflow regression paths, including saved parametric flex/material assertions.
   foreach(bool parametric in new[]{false,true}){var result=FamilyFileWorkflow.CreateFamily(ui.Application,parametric);Check(result.Status==Result.Succeeded,"Native regression "+parametric);}
   var manual=FamilyFileWorkflow.CreateFamily(ui.Application,false,new(Path.Combine(root,"tests/fixtures/cube_500mm.obj"),ObjUnit.Millimeters,ObjVerticalAxis.Z,500,"Stage5A_Manual","Regression"));Check(manual.Status==Result.Succeeded,"Manual OBJ workflow regression");
   var photo=PhotoFamilyRequest.Create(new("Simulation","local",Path.Combine(root,"tests/fixtures/cube_500mm.obj"),new(500,500,500)),750,ObjVerticalAxis.Z,"Stage5A_Photo","Regression",true);
   Check(FamilyFileWorkflow.CreateFamily(ui.Application,false,photo).Status==Result.Succeeded,"Simulated photo contract workflow regression");
   doc=ui.Application.NewProjectDocument(UnitSystem.Metric);
   int x=0;
   foreach(string path in paths)
   {
    using var t=new Transaction(doc,"Load and place test family");t.Start();
    Check(doc.LoadFamily(path,out var family),"Load family "+Path.GetFileName(path));
    var symbol=(FamilySymbol)doc.GetElement(family.GetFamilySymbolIds().First());symbol.Activate();doc.Regenerate();var instance=doc.Create.NewFamilyInstance(new XYZ(x++*4,0,0),symbol,StructuralType.NonStructural);Check(instance is not null,"Place instance");doc.Regenerate();
    var colors=new List<string>();
    void InspectPlaced(GeometryElement geometry){foreach(var item in geometry){if(item is GeometryInstance gi)InspectPlaced(gi.GetInstanceGeometry());if(item is Mesh mesh){var mat=doc.GetElement(mesh.MaterialElementId) as Material ?? (doc.GetElement(mesh.GraphicsStyleId) as GraphicsStyle)?.GraphicsStyleCategory.Material;colors.Add(mat is null?"none":$"{mat.Color.Red},{mat.Color.Green},{mat.Color.Blue}");}}}
    InspectPlaced(instance!.get_Geometry(new Options()));log.Add(new{placed=Path.GetFileName(path),colors});
    string label=Path.GetFileNameWithoutExtension(path);string? expected=label.EndsWith("Blue") || label.EndsWith("Chair")?"0,40,255":label.EndsWith("Red") || label.EndsWith("MultiUniform")?"240,20,20":null;
    if(expected is not null)Check(colors.Count>0 && colors.All(c=>c==expected),"Placed RGB "+label);
    if(label.EndsWith("MultiTwo"))Check(colors.Contains("240,20,20") && colors.Contains("0,40,255"),"Placed independent red and blue");
    if(label.EndsWith("MultiSingle"))Check(colors.Contains("240,20,20") && colors.Any(c=>c!="240,20,20"),"Placed edited and untouched colors");
    t.Commit();
   }
   ElementId shade;
   using(var t=new Transaction(doc,"Project views")){t.Start();var type=new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(v=>v.ViewFamily==ViewFamily.ThreeDimensional);var view=View3D.CreateIsometric(doc,type.Id);view.Name="Stage5A_Shaded";view.DisplayStyle=DisplayStyle.Shading;shade=view.Id;var real=View3D.CreateIsometric(doc,type.Id);real.Name="Stage5A_Realistic";real.DisplayStyle=DisplayStyle.Realistic;t.Commit();}
   string project=Path.Combine(output,"Stage5A_Materials.rvt");doc.SaveAs(project,new SaveAsOptions{OverwriteExistingFile=false});doc.Close(false);doc=null;
   var opened=ui.OpenAndActivateDocument(project);opened.RequestViewChange(opened.Document.GetElement(shade) as View);Check(true,"Saved and reopened project for visual inspection");
   foreach(FamilyInstance instance in new FilteredElementCollector(opened.Document).OfClass(typeof(FamilyInstance)))
   {
    string label=instance.Symbol.Family.Name;
    if(label is not ("Stage5A_MultiTwo" or "Stage5A_MultiSingle"))continue;
    var colors=new List<string>();
    void Walk(GeometryElement geometry){foreach(var item in geometry){if(item is GeometryInstance gi)Walk(gi.GetInstanceGeometry());if(item is Mesh mesh && opened.Document.GetElement(mesh.MaterialElementId) is Material m)colors.Add($"{m.Color.Red},{m.Color.Green},{m.Color.Blue}");}}
    Walk(instance.get_Geometry(new Options()));
    Check(colors.Contains("240,20,20") && (label.EndsWith("MultiTwo")?colors.Contains("0,40,255"):colors.Any(c=>c!="240,20,20")),"Reopened RVT distinct "+label);
   }
   doc=ui.Application.OpenDocumentFile(Path.Combine(output,"Stage5A_MultiTwo.rfa"));
   var uiOriginals=new HashSet<ElementId>(); // This isolated test opens an already validated output family.
   var uiRequest=new ObjImportRequest(Path.Combine(root,"tests/fixtures/material_regions.obj"),ObjUnit.Millimeters,ObjVerticalAxis.Z,500,"Stage5A_MultiTwo","Manual dialog validation");
   string uiGeometry=Signature(doc);
   var canceled=new ImportedMaterialDialog(doc,"Stage5C_Dialog",uiOriginals);
   Check(canceled.ShowDialog()!=true && canceled.MultipleSelected is null,"Dialog cancellation publishes no selection");
   var dialog=new ImportedMaterialDialog(doc,"Stage5C_Dialog",uiOriginals);
   Check(dialog.ShowDialog()==true && dialog.MultipleSelected?.Count==2,"Dialog selected two independent RGB assignments");
   ImportedMaterials.ApplyMultiple(doc,dialog.MultipleSelected!,uiOriginals);
   var uiExpected=ImportedMaterials.Snapshot(doc);Check(Signature(doc)==uiGeometry,"Dialog material edit preserves geometry");
   string uiPath=Path.Combine(output,"Stage5C_Dialog.rfa");doc.SaveAs(uiPath,new SaveAsOptions{OverwriteExistingFile=false});doc.Close(false);doc=null;
   doc=ui.Application.OpenDocumentFile(uiPath);ObjFamilyImporter.ValidateSaved(doc,uiRequest);ImportedMaterials.ValidateSnapshot(doc,uiExpected);Check(Signature(doc)==uiGeometry,"Dialog RFA reopened with independent materials");doc.Close(false);doc=null;
  }
  catch(Exception ex){log.Add(new {error=ex.ToString()});}
  finally{doc?.Close(false);File.WriteAllText(Path.Combine(output,"results.json"),JsonSerializer.Serialize(new{passed,log},new JsonSerializerOptions{WriteIndented=true}));}
 }
}

