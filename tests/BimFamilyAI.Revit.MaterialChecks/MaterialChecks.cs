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
  string root=Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(MaterialChecks).Assembly.Location)!,"../../../../../"));
  string output=Path.Combine(root,"artifacts","stage5a-"+DateTime.Now.ToString("yyyyMMdd-HHmmss"));Directory.CreateDirectory(output);
  var log=new List<object>();Document? doc=null;int passed=0;
  void Check(bool condition,string label){if(!condition)throw new Exception(label);passed++;log.Add(new {pass=label});}
  string Signature(Document d){var lines=new List<string>();void Walk(GeometryElement g){foreach(var item in g){if(item is GeometryInstance i)Walk(i.GetInstanceGeometry());if(item is Mesh m){lines.Add("tri="+m.NumTriangles);foreach(var p in m.Vertices)lines.Add(p.ToString());}}}foreach(ImportInstance i in new FilteredElementCollector(d).OfClass(typeof(ImportInstance)))Walk(i.get_Geometry(new Options()));return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("|",lines))));}
  try
  {
   var paths=new List<string>();
   foreach(var (label,file,color,axis) in new[]{("Blue","cube_500mm.obj",(0,40,255),ObjVerticalAxis.Z),("Red","cube_500mm.obj",(240,20,20),ObjVerticalAxis.Z),("Original","cube_500mm.obj",(-1,0,0),ObjVerticalAxis.Z),("Multi","material_regions.obj",(-2,0,0),ObjVerticalAxis.Z),("MultiUniform","material_regions.obj",(240,20,20),ObjVerticalAxis.Z),("Chair","chair.obj",(0,40,255),ObjVerticalAxis.Y)})
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
    if(label=="Original")Check(new FilteredElementCollector(doc).OfClass(typeof(Material)).GetElementCount()==originals.Count,"Preserve adds no materials");
    Check(Signature(doc)==geometry,label+" geometry unchanged");
    Check(originalColors.All(p=>{var c=((Material)doc.GetElement(p.Key)).Color;return c.Red==p.Value.Red && c.Green==p.Value.Green && c.Blue==p.Value.Blue;}),label+" template materials unchanged");
    using(var t=new Transaction(doc,"Shaded test view")){t.Start();var v=View3D.CreateIsometric(doc,new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(v=>v.ViewFamily==ViewFamily.ThreeDimensional).Id);v.Name="Stage5A_Shaded";v.DisplayStyle=DisplayStyle.Shading;t.Commit();}
    string path=Path.Combine(output,"Stage5A_"+label+".rfa");doc.SaveAs(path,new SaveAsOptions{OverwriteExistingFile=false});doc.Close(false);doc=null;
    doc=ui.Application.OpenDocumentFile(path);ObjFamilyImporter.ValidateSaved(doc,request);Check(Signature(doc)==geometry,label+" reopened geometry and dimensions");
    if(color.Item1>=0){ImportedMaterials.Validate(doc,(byte)color.Item1,(byte)color.Item2,(byte)color.Item3);Check(true,label+" persisted material RGB");}
    if(label=="Multi")Check(ImportedMaterials.Inspect(doc).Select(r=>r.MaterialId).Distinct().Count()==2,"Two material references persist");
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
    t.Commit();
   }
   ElementId shade;
   using(var t=new Transaction(doc,"Project views")){t.Start();var type=new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(v=>v.ViewFamily==ViewFamily.ThreeDimensional);var view=View3D.CreateIsometric(doc,type.Id);view.Name="Stage5A_Shaded";view.DisplayStyle=DisplayStyle.Shading;shade=view.Id;var real=View3D.CreateIsometric(doc,type.Id);real.Name="Stage5A_Realistic";real.DisplayStyle=DisplayStyle.Realistic;t.Commit();}
   string project=Path.Combine(output,"Stage5A_Materials.rvt");doc.SaveAs(project,new SaveAsOptions{OverwriteExistingFile=false});doc.Close(false);doc=null;
   var opened=ui.OpenAndActivateDocument(project);opened.RequestViewChange(opened.Document.GetElement(shade) as View);Check(true,"Saved and reopened project for visual inspection");
  }
  catch(Exception ex){log.Add(new {error=ex.ToString()});}
  finally{doc?.Close(false);File.WriteAllText(Path.Combine(output,"results.json"),JsonSerializer.Serialize(new{passed,log},new JsonSerializerOptions{WriteIndented=true}));}
 }
}

